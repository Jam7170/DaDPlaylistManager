using System.Security.Cryptography;

namespace DaDPlaylistManager.Core.Songs;

/// <summary>An imported song folder: Saved/ImportedSongs/&lt;FolderName&gt;/{Audio.ogg, Meta.json}.</summary>
public sealed class SongEntry
{
    public required string Directory { get; init; }
    public string FolderName => Path.GetFileName(Directory);
    public string MetaPath => Path.Combine(Directory, SongLibrary.MetaFileName);
    public string AudioPath => Path.Combine(Directory, SongLibrary.AudioFileName);
    public bool HasAudio => File.Exists(AudioPath);

    /// <summary>Null when Meta.json is missing or unreadable (see <see cref="LoadError"/>).</summary>
    public SongMeta? Meta { get; set; }
    public string? LoadError { get; init; }

    public string Title => Meta?.SongName is { Length: > 0 } n ? n : FolderName;
    public string Artists => Meta?.ArtistsDisplay ?? string.Empty;
}

/// <summary>Reads and writes the game's ImportedSongs folder.</summary>
public sealed class SongLibrary(string importedSongsDirectory)
{
    public const string AudioFileName = "Audio.ogg";
    public const string MetaFileName = "Meta.json";

    public string Root { get; } = importedSongsDirectory;

    public List<SongEntry> Load()
    {
        var list = new List<SongEntry>();
        if (!System.IO.Directory.Exists(Root)) return list;
        foreach (var dir in System.IO.Directory.EnumerateDirectories(Root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            string metaPath = Path.Combine(dir, MetaFileName);
            if (!File.Exists(metaPath))
            {
                list.Add(new SongEntry { Directory = dir, LoadError = "Meta.json is missing" });
                continue;
            }
            try
            {
                list.Add(new SongEntry { Directory = dir, Meta = SongMeta.Load(metaPath) });
            }
            catch (Exception ex)
            {
                list.Add(new SongEntry { Directory = dir, LoadError = "Meta.json could not be read: " + ex.Message });
            }
        }
        return list;
    }

    /// <summary>
    /// Folder name the game would use for a song: "Artist1, Artist2 - Title", with characters that
    /// aren't valid in a file name removed.
    /// </summary>
    public static string BuildFolderName(IEnumerable<string> artists, string title)
    {
        string artistPart = string.Join(", ", artists.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()));
        string raw = artistPart.Length > 0 ? $"{artistPart} - {title.Trim()}" : title.Trim();
        return SanitizeFolderName(raw);
    }

    public static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Where(c => !invalid.Contains(c) && !char.IsControl(c)).ToArray();
        string result = new string(chars).Trim().TrimEnd('.', ' ');
        while (result.Contains("  ")) result = result.Replace("  ", " ");
        return result.Length == 0 ? "Untitled" : result;
    }

    /// <summary>Returns <paramref name="desired"/>, or "desired (2)", "desired (3)"... if it's taken.</summary>
    public string MakeUniqueFolderName(string desired, string? ignoreExistingDirectory = null)
    {
        string candidate = desired;
        for (int n = 2; ; n++)
        {
            string path = Path.Combine(Root, candidate);
            bool taken = System.IO.Directory.Exists(path) &&
                         !string.Equals(Path.GetFullPath(path), ignoreExistingDirectory is null ? null : Path.GetFullPath(ignoreExistingDirectory), StringComparison.OrdinalIgnoreCase);
            if (!taken) return candidate;
            candidate = $"{desired} ({n})";
        }
    }

    public static uint NewUniqueId(IEnumerable<uint> existing)
    {
        var used = existing.ToHashSet();
        while (true)
        {
            uint id = SongMeta.RandomUInt();
            if (id != 0 && !used.Contains(id)) return id;
        }
    }

    /// <summary>Game-style path string stored in originalAudioFilePath.</summary>
    public static string GameStylePath(string path) => Path.GetFullPath(path).Replace('\\', '/');

    public static string Md5Hex(string file)
    {
        using var s = File.OpenRead(file);
        return Md5Hex(s);
    }

    public static string Md5Hex(Stream s) => Convert.ToHexStringLower(MD5.HashData(s));

    public void SaveMeta(SongEntry song)
    {
        if (song.Meta is null) throw new InvalidOperationException("Song has no metadata to save.");
        song.Meta.Save(song.MetaPath);
    }

    /// <summary>Renames the song folder and updates originalAudioFilePath. Returns the new entry.</summary>
    public SongEntry Rename(SongEntry song, string newFolderName)
    {
        string target = Path.Combine(Root, newFolderName);
        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(song.Directory), StringComparison.Ordinal))
            return song;

        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(song.Directory), StringComparison.OrdinalIgnoreCase))
        {
            // Case-only rename on a case-insensitive file system needs a hop through a temp name.
            string temp = song.Directory.TrimEnd('\\', '/') + ".renaming";
            System.IO.Directory.Move(song.Directory, temp);
            System.IO.Directory.Move(temp, target);
        }
        else
        {
            if (System.IO.Directory.Exists(target)) throw new IOException($"A song folder named '{newFolderName}' already exists.");
            System.IO.Directory.Move(song.Directory, target);
        }

        var renamed = new SongEntry { Directory = target, Meta = song.Meta };
        if (renamed.Meta is not null)
        {
            renamed.Meta.OriginalAudioFilePath = GameStylePath(renamed.AudioPath);
            SaveMeta(renamed);
        }
        return renamed;
    }
}
