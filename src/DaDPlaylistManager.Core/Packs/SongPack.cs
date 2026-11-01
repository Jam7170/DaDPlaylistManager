using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using DaDPlaylistManager.Core.Playlists;
using DaDPlaylistManager.Core.Songs;

namespace DaDPlaylistManager.Core.Packs;

/// <summary>
/// A shareable bundle of songs (and optionally playlists): a zip containing
/// <c>manifest.json</c> plus <c>songs/&lt;folder&gt;/{Audio.ogg, Meta.json}</c>.
/// Importing also accepts plain zips of ImportedSongs folders without a manifest.
/// </summary>
public static class SongPack
{
    public const string Extension = ".dadpack";
    public const int FormatVersion = 1;
    private const string ManifestName = "manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public sealed class Manifest
    {
        public int Format { get; set; } = FormatVersion;
        public string App { get; set; } = "DaDPlaylistManager";
        public DateTime Created { get; set; } = DateTime.UtcNow;
        public List<ManifestSong> Songs { get; set; } = [];
        public List<ManifestPlaylist> Playlists { get; set; } = [];
    }

    public sealed class ManifestSong
    {
        public string Folder { get; set; } = "";
        public uint UniqueId { get; set; }
        public string Title { get; set; } = "";
        public string Artists { get; set; } = "";
    }

    public sealed class ManifestPlaylist
    {
        public string Name { get; set; } = "";
        public bool Shuffle { get; set; }
        /// <summary>Each item is either an imported song id or a raw object path (built-in songs).</summary>
        public List<ManifestPlaylistItem> Items { get; set; } = [];
    }

    public sealed class ManifestPlaylistItem
    {
        public uint? SongId { get; set; }
        public string? ObjectPath { get; set; }
    }

    public sealed record ImportResult(int Added, int AlreadyPresent, int IdsReassigned, int PlaylistsAdded, List<string> Warnings);

    public static void Export(string zipPath, IReadOnlyCollection<SongEntry> songs, IReadOnlyCollection<PlaylistFile> playlists,
        IProgress<double>? progress = null)
    {
        var manifest = new Manifest();
        string tmp = zipPath + ".tmp";
        using (var fs = File.Create(tmp))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            int done = 0;
            foreach (var song in songs)
            {
                if (song.Meta is null || !song.HasAudio) continue;
                string folder = song.FolderName;
                var meta = song.Meta.Clone();
                meta.OriginalAudioFilePath = ""; // machine-specific; rewritten on import
                zip.CreateEntryFromFile(song.AudioPath, $"songs/{folder}/{SongLibrary.AudioFileName}", CompressionLevel.NoCompression);
                var metaEntry = zip.CreateEntry($"songs/{folder}/{SongLibrary.MetaFileName}", CompressionLevel.Optimal);
                using (var w = new StreamWriter(metaEntry.Open())) w.Write(meta.ToJson());
                manifest.Songs.Add(new ManifestSong { Folder = folder, UniqueId = meta.UniqueId, Title = meta.SongName, Artists = meta.ArtistsDisplay });
                progress?.Report(++done / (double)songs.Count);
            }

            foreach (var pl in playlists)
            {
                manifest.Playlists.Add(new ManifestPlaylist
                {
                    Name = pl.Name,
                    Shuffle = pl.Shuffle,
                    Items = pl.Entries.Select(e => e.ImportedSongId is { } id
                        ? new ManifestPlaylistItem { SongId = id }
                        : new ManifestPlaylistItem { ObjectPath = e.ObjectPath }).ToList(),
                });
            }

            var me = zip.CreateEntry(ManifestName, CompressionLevel.Optimal);
            using var ms = me.Open();
            JsonSerializer.Serialize(ms, manifest, JsonOptions);
        }
        File.Move(tmp, zipPath, overwrite: true);
    }

    /// <summary>Lists what's in a pack without changing anything.</summary>
    public static Manifest ReadManifest(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return ReadOrBuildManifest(zip);
    }

    public static ImportResult Import(string zipPath, SongLibrary library, IReadOnlyList<SongEntry> existingSongs, PlaylistStore playlists,
        string transientPrefix, bool importPlaylists = true, IProgress<double>? progress = null)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var manifest = ReadOrBuildManifest(zip);
        var warnings = new List<string>();

        var byId = existingSongs.Where(s => s.Meta is not null).GroupBy(s => s.Meta!.UniqueId).ToDictionary(g => g.Key, g => g.First());
        var byHash = existingSongs.Where(s => s.Meta is { OriginalAudioFileHash.Length: > 0 })
            .GroupBy(s => s.Meta!.OriginalAudioFileHash, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var usedIds = new HashSet<uint>(byId.Keys);

        // Pack song id -> (local id, local folder name)
        var idMap = new Dictionary<uint, (uint Id, string Folder)>();
        int added = 0, present = 0, reassigned = 0, done = 0;

        foreach (var ms in manifest.Songs)
        {
            progress?.Report(done++ / (double)Math.Max(1, manifest.Songs.Count));
            string prefix = FindSongPrefix(zip, ms.Folder);
            var metaEntry = zip.GetEntry(prefix + SongLibrary.MetaFileName);
            var audioEntry = zip.GetEntry(prefix + SongLibrary.AudioFileName);
            if (metaEntry is null || audioEntry is null)
            {
                warnings.Add($"Skipped '{ms.Folder}': Audio.ogg or Meta.json missing in pack.");
                continue;
            }

            SongMeta meta;
            try
            {
                using var r = new StreamReader(metaEntry.Open());
                meta = SongMeta.Parse(r.ReadToEnd());
            }
            catch (Exception ex)
            {
                warnings.Add($"Skipped '{ms.Folder}': bad Meta.json ({ex.Message}).");
                continue;
            }

            string hash;
            using (var a = audioEntry.Open()) hash = SongLibrary.Md5Hex(a);

            // Already have this exact audio? Reuse the local song.
            var local = byHash.GetValueOrDefault(hash);
            if (local is null && byId.TryGetValue(meta.UniqueId, out var sameId) && sameId.HasAudio && SongLibrary.Md5Hex(sameId.AudioPath) == hash)
                local = sameId;
            if (local is not null)
            {
                idMap[ms.UniqueId] = (local.Meta!.UniqueId, local.FolderName);
                present++;
                continue;
            }

            uint packId = meta.UniqueId;
            if (packId == 0 || usedIds.Contains(packId))
            {
                meta.UniqueId = SongLibrary.NewUniqueId(usedIds);
                reassigned++;
            }
            usedIds.Add(meta.UniqueId);

            string folder = library.MakeUniqueFolderName(SongLibrary.SanitizeFolderName(ms.Folder.Length > 0 ? ms.Folder : SongLibrary.BuildFolderName(meta.PerformedBy, meta.SongName)));
            string dir = Path.Combine(library.Root, folder);
            Directory.CreateDirectory(dir);
            string audioPath = Path.Combine(dir, SongLibrary.AudioFileName);
            audioEntry.ExtractToFile(audioPath, overwrite: true);
            meta.OriginalAudioFileHash = hash;
            meta.OriginalAudioFilePath = SongLibrary.GameStylePath(audioPath);
            meta.Save(Path.Combine(dir, SongLibrary.MetaFileName));

            byHash[hash] = new SongEntry { Directory = dir, Meta = meta };
            idMap[ms.UniqueId] = (meta.UniqueId, folder);
            added++;
        }

        int playlistsAdded = 0;
        if (importPlaylists)
        {
            var existingNames = playlists.Load().Select(p => p.File?.Name).OfType<string>().ToHashSet(StringComparer.CurrentCultureIgnoreCase);
            foreach (var mp in manifest.Playlists)
            {
                var pl = new PlaylistFile { Name = UniqueName(string.IsNullOrWhiteSpace(mp.Name) ? "Imported Playlist" : mp.Name, existingNames), Shuffle = mp.Shuffle };
                foreach (var item in mp.Items)
                {
                    if (item.SongId is { } sid)
                    {
                        if (idMap.TryGetValue(sid, out var local))
                            pl.Entries.Add(new PlaylistEntry(PlaylistFile.ImportedObjectPath(transientPrefix, local.Folder), local.Id));
                        else
                            warnings.Add($"Playlist '{mp.Name}': a song (id {sid}) wasn't in the pack and was left out.");
                    }
                    else if (!string.IsNullOrEmpty(item.ObjectPath))
                    {
                        pl.Entries.Add(new PlaylistEntry(item.ObjectPath, null));
                    }
                }
                existingNames.Add(pl.Name);
                playlists.Save(pl);
                playlistsAdded++;
            }
        }

        progress?.Report(1);
        return new ImportResult(added, present, reassigned, playlistsAdded, warnings);
    }

    private static string UniqueName(string name, HashSet<string> taken)
    {
        if (!taken.Contains(name)) return name;
        for (int n = 2; ; n++)
        {
            string candidate = $"{name} ({n})";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    private static Manifest ReadOrBuildManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry(ManifestName);
        if (entry is not null)
        {
            using var s = entry.Open();
            var m = JsonSerializer.Deserialize<Manifest>(s, JsonOptions) ?? throw new InvalidDataException("Empty manifest.");
            if (m.Format > FormatVersion) throw new InvalidDataException($"This pack was made by a newer version (format {m.Format}).");
            return m;
        }

        // No manifest: treat every folder that contains Meta.json + Audio.ogg as a song.
        var manifest = new Manifest();
        foreach (var e in zip.Entries.Where(e => e.Name.Equals(SongLibrary.MetaFileName, StringComparison.OrdinalIgnoreCase)))
        {
            string dirPrefix = e.FullName[..^e.Name.Length];
            if (zip.GetEntry(dirPrefix + SongLibrary.AudioFileName) is null) continue;
            string folder = dirPrefix.TrimEnd('/').Split('/').LastOrDefault() ?? "";
            uint id = 0;
            try
            {
                using var r = new StreamReader(e.Open());
                id = SongMeta.Parse(r.ReadToEnd()).UniqueId;
            }
            catch { }
            manifest.Songs.Add(new ManifestSong { Folder = folder, UniqueId = id });
        }
        if (manifest.Songs.Count == 0) throw new InvalidDataException("No songs found in this file.");
        return manifest;
    }

    private static string FindSongPrefix(ZipArchive zip, string folder)
    {
        string standard = $"songs/{folder}/";
        if (zip.GetEntry(standard + SongLibrary.MetaFileName) is not null) return standard;
        var match = zip.Entries.FirstOrDefault(e =>
            e.Name.Equals(SongLibrary.MetaFileName, StringComparison.OrdinalIgnoreCase) &&
            e.FullName[..^e.Name.Length].TrimEnd('/').EndsWith(folder, StringComparison.Ordinal));
        return match is null ? standard : match.FullName[..^match.Name.Length];
    }
}
