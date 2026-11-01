using DaDPlaylistManager.Core.Songs;

namespace DaDPlaylistManager.Core.Audio;

/// <summary>Suggested metadata for an audio file about to be imported.</summary>
public sealed record ImportGuess(string Title, List<string> Artists);

/// <summary>Converts arbitrary audio files into game-ready ImportedSongs folders.</summary>
public sealed class AudioImporter(Ffmpeg ffmpeg, SongLibrary library)
{
    public const int AnalysisSampleRate = 22050;

    public static readonly string[] SupportedExtensions =
        [".mp3", ".wav", ".flac", ".ogg", ".oga", ".opus", ".m4a", ".aac", ".wma", ".aiff", ".aif", ".alac", ".webm", ".mp4", ".mkv"];

    /// <summary>Guesses title/artists from tags, falling back to "Artist - Title" in the file name.</summary>
    public async Task<ImportGuess> GuessMetadataAsync(string file, CancellationToken ct = default)
    {
        var tags = await ffmpeg.ReadTagsAsync(file, ct);
        tags.TryGetValue("title", out var title);
        tags.TryGetValue("artist", out var artist);
        if (string.IsNullOrWhiteSpace(artist)) tags.TryGetValue("album_artist", out artist);

        string stem = Path.GetFileNameWithoutExtension(file);
        int dash = stem.IndexOf(" - ", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(title)) title = dash > 0 ? stem[(dash + 3)..] : stem;
        if (string.IsNullOrWhiteSpace(artist) && dash > 0) artist = stem[..dash];

        var artists = SplitArtists(artist ?? "");
        return new ImportGuess(title.Trim(), artists);
    }

    public static List<string> SplitArtists(string artist) =>
        artist.Split([";", "/", "\0"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public async Task<BpmDetector.Result> AnalyzeAsync(string file, CancellationToken ct = default)
    {
        var pcm = await ffmpeg.DecodeMonoAsync(file, AnalysisSampleRate, ct);
        return await Task.Run(() => BpmDetector.Detect(pcm, AnalysisSampleRate), ct);
    }

    /// <summary>
    /// Creates a new ImportedSongs folder with Audio.ogg and Meta.json. Returns the new entry.
    /// </summary>
    public async Task<SongEntry> ImportAsync(string sourceFile, string title, IReadOnlyList<string> artists, double tempo,
        int beatOffsetMs, IEnumerable<uint> existingIds, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("A title is required.");
        if (tempo <= 0) throw new ArgumentException("Tempo must be greater than zero.");

        Directory.CreateDirectory(library.Root);
        string folder = library.MakeUniqueFolderName(SongLibrary.BuildFolderName(artists, title));
        string dir = Path.Combine(library.Root, folder);
        string temp = Path.Combine(library.Root, $".importing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            string tempOgg = Path.Combine(temp, SongLibrary.AudioFileName);
            await ffmpeg.ConvertToOggAsync(sourceFile, tempOgg, ct);

            var meta = SongMeta.CreateNew(SongLibrary.NewUniqueId(existingIds), title.Trim(), artists, tempo, beatOffsetMs);
            meta.OriginalAudioFileHash = SongLibrary.Md5Hex(tempOgg);
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
}
