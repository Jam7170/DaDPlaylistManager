using System.Text.Json.Nodes;
using DaDPlaylistManager.Core.Playlists;
using DaDPlaylistManager.Core.Songs;

namespace DaDPlaylistManager.Core.Sync;

/// <summary>Remembers MD5s of local Audio.ogg files so the library isn't re-hashed on every sync.</summary>
public sealed class AudioHashCache
{
    private readonly Dictionary<string, (long Size, DateTime Modified, string Hash)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public string Get(string audioPath)
    {
        var info = new FileInfo(audioPath);
        lock (_lock)
        {
            if (_cache.TryGetValue(audioPath, out var c) && c.Size == info.Length && c.Modified == info.LastWriteTimeUtc) return c.Hash;
        }
        string hash = SongLibrary.Md5Hex(audioPath);
        lock (_lock) _cache[audioPath] = (info.Length, info.LastWriteTimeUtc, hash);
        return hash;
    }

    /// <summary>Hash → song for every local song with audio and metadata.</summary>
    public Dictionary<string, SongEntry> Index(IEnumerable<SongEntry> songs)
    {
        var map = new Dictionary<string, SongEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in songs.Where(s => s.Meta is not null && s.HasAudio))
            map.TryAdd(Get(s.AudioPath), s);
        return map;
    }
}

public enum SharedSongState
{
    /// <summary>On this PC with the same settings as the shared copy.</summary>
    Synced,
    /// <summary>Not on this PC yet.</summary>
    Missing,
    /// <summary>On this PC, but its timing/details differ from the shared copy.</summary>
    Differs,
    /// <summary>You shared it and changed it locally since; Sync will upload your version.</summary>
    LocalChanges,
}

public sealed record SyncResult(int Downloaded, int Updated, int Pushed, string LocalSetCode, string ServerSetCode, string LocalPlaylistPath, List<string> Warnings)
{
    public bool InSync => LocalSetCode == ServerSetCode;
}

/// <summary>Uploads, downloads and reconciles songs between the local game folder and a shared playlist.</summary>
public sealed class SharedSync(SyncClient client, SongLibrary library, PlaylistStore playlists, BackupService backups, AudioHashCache hashes)
{
    public const string LocalNameSuffix = " (shared)";

    /// <summary>Meta as it should be stored on the server: no machine-specific path.</summary>
    public static JsonObject ServerMeta(SongMeta meta)
    {
        var copy = meta.Clone();
        copy.OriginalAudioFilePath = "";
        return (JsonObject)JsonNode.Parse(copy.ToCompactJson())!;
    }

    public static SongMeta ToMeta(JsonObject json) => SongMeta.Parse(json.ToJsonString());

    public static SharedSongState StateOf(SharedSong song, Dictionary<string, SongEntry> local, int myPlayerId)
    {
        if (!local.TryGetValue(song.Hash, out var entry)) return SharedSongState.Missing;
        if (entry.Meta!.SharedFieldsEqual(ToMeta(song.Meta))) return SharedSongState.Synced;
        return song.UploadedBy.Id == myPlayerId ? SharedSongState.LocalChanges : SharedSongState.Differs;
    }

    /// <summary>Uploads songs (audio and metadata, skipping what the server has) and adds them to the playlist.</summary>
    public async Task<SharedPlaylist> ShareSongsAsync(int playlistId, IReadOnlyList<SongEntry> songs, int myPlayerId,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var added = new List<string>();
        int i = 0;
        foreach (var song in songs)
        {
            ct.ThrowIfCancellationRequested();
            if (song.Meta is null || !song.HasAudio) continue;
            progress?.Report($"Uploading {++i}/{songs.Count}: {song.Title}");
            string hash = await Task.Run(() => hashes.Get(song.AudioPath), ct);
            var info = await client.GetSongAsync(hash, ct);
            if (info is null || !info.HasAudio) await client.UploadAudioAsync(hash, song.AudioPath, ct);
            bool canSetMeta = info?.Meta is null || info.UploadedBy is null || info.UploadedBy.Id == myPlayerId;
            if (canSetMeta && (info?.Meta is null || !song.Meta.SharedFieldsEqual(ToMeta(info.Meta))))
                await client.UploadMetaAsync(hash, ServerMeta(song.Meta), ct);
            added.Add(hash);
        }
        return await client.AddSongsAsync(playlistId, added, ct);
    }

    /// <summary>
    /// Brings this PC in line with the shared playlist: pushes your own edits to songs you shared,
    /// downloads missing songs, adopts everyone else's timing, and writes the local game playlist.
    /// </summary>
    /// <param name="localPlaylistGuid">GUID (hex) of the local playlist previously written for this shared playlist, if any.</param>
    public async Task<(SyncResult Result, string LocalGuidHex)> SyncAsync(int playlistId, int myPlayerId, string? localPlaylistGuid,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        progress?.Report("Fetching playlist…");
        var shared = await client.GetPlaylistAsync(playlistId, ct);

        progress?.Report("Checking your songs…");
        var localSongs = library.Load();
        var local = await Task.Run(() => hashes.Index(localSongs), ct);
        var usedIds = localSongs.Where(s => s.Meta is not null).Select(s => s.Meta!.UniqueId).ToHashSet();

        int downloaded = 0, updated = 0, pushed = 0, n = 0;
        foreach (var song in shared.Songs)
        {
            ct.ThrowIfCancellationRequested();
            n++;
            var serverMeta = ToMeta(song.Meta);
            if (local.TryGetValue(song.Hash, out var entry))
            {
                if (entry.Meta!.SharedFieldsEqual(serverMeta)) continue;
                if (song.UploadedBy.Id == myPlayerId)
                {
                    progress?.Report($"Uploading your changes to {entry.Title}…");
                    await client.UploadMetaAsync(song.Hash, ServerMeta(entry.Meta), ct);
                    pushed++;
                }
                else
                {
                    backups.BackupSongMetadata(entry.Directory);
                    entry.Meta.AdoptFrom(serverMeta);
                    library.SaveMeta(entry);
                    updated++;
                }
                continue;
            }

            progress?.Report($"Downloading {n}/{shared.Songs.Count}: {serverMeta.SongName}");
            try
            {
                local[song.Hash] = await DownloadSongAsync(song, serverMeta, usedIds, ct);
                downloaded++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"Couldn't download \"{serverMeta.SongName}\": {ex.Message}");
            }
        }

        // Pushing may have changed the server's set code; refetch if so.
        if (pushed > 0) shared = await client.GetPlaylistAsync(playlistId, ct);

        progress?.Report("Writing the game playlist…");
        var (path, guidHex) = WriteLocalPlaylist(shared, local, localPlaylistGuid);

        var present = shared.Songs.Where(s => local.ContainsKey(s.Hash)).ToList();
        if (present.Count < shared.Songs.Count)
            warnings.Add($"{shared.Songs.Count - present.Count} song(s) are still missing, so the set code won't match yet.");
        string localCode = SetCode.Compute(present.Select(s => (s.Hash, local[s.Hash].Meta!)));
        try
        {
            await client.ReportSyncedAsync(playlistId, localCode, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add("Couldn't report sync status: " + ex.Message);
        }

        return (new SyncResult(downloaded, updated, pushed, localCode, shared.SetCode, path, warnings), guidHex);
    }

    private async Task<SongEntry> DownloadSongAsync(SharedSong song, SongMeta serverMeta, HashSet<uint> usedIds, CancellationToken ct)
    {
        Directory.CreateDirectory(library.Root);
        string folder = library.MakeUniqueFolderName(SongLibrary.BuildFolderName(serverMeta.PerformedBy, serverMeta.SongName));
        string dir = Path.Combine(library.Root, folder);
        string temp = Path.Combine(library.Root, $".downloading-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            string tempAudio = Path.Combine(temp, SongLibrary.AudioFileName);
            await client.DownloadAudioAsync(song.Hash, tempAudio, ct);
            string actual = SongLibrary.Md5Hex(tempAudio);
            if (!actual.Equals(song.Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("the downloaded file was corrupted (hash mismatch)");

            var meta = serverMeta.Clone();
            if (meta.UniqueId == 0 || usedIds.Contains(meta.UniqueId)) meta.UniqueId = SongLibrary.NewUniqueId(usedIds);
            usedIds.Add(meta.UniqueId);
            meta.OriginalAudioFileHash = actual;
            meta.OriginalAudioFilePath = SongLibrary.GameStylePath(Path.Combine(dir, SongLibrary.AudioFileName));
            meta.Save(Path.Combine(temp, SongLibrary.MetaFileName));
            Directory.Move(temp, dir);
            return new SongEntry { Directory = dir, Meta = meta };
        }
        catch
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
            throw;
        }
    }

    private (string Path, string GuidHex) WriteLocalPlaylist(SharedPlaylist shared, Dictionary<string, SongEntry> local, string? guidHex)
    {
        var docs = playlists.Load();
        var existing = guidHex is null ? null : docs.FirstOrDefault(d => d.File?.GuidHex.Equals(guidHex, StringComparison.OrdinalIgnoreCase) == true);
        var file = existing?.File ?? new PlaylistFile();
        string prefix = docs.Select(d => d.File).OfType<PlaylistFile>().Select(f => f.TransientPrefix).FirstOrDefault() ?? PlaylistFile.DefaultTransientPrefix;

        file.Name = shared.Name + LocalNameSuffix;
        file.Shuffle = shared.Shuffle;
        file.Entries.Clear();
        foreach (var s in shared.Songs)
        {
            if (!local.TryGetValue(s.Hash, out var entry)) continue;
            file.Entries.Add(new PlaylistEntry(PlaylistFile.ImportedObjectPath(prefix, entry.FolderName), entry.Meta!.UniqueId));
        }
        return (playlists.Save(file), file.GuidHex);
    }
}
