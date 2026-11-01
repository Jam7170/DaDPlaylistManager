namespace DaDPlaylistManager.Core.Playlists;

public sealed class PlaylistDocument
{
    public required string FilePath { get; set; }
    public PlaylistFile? File { get; init; }
    public string? LoadError { get; init; }
}

/// <summary>Reads and writes the game's Playlists folder.</summary>
public sealed class PlaylistStore(string playlistsDirectory, BackupService backups)
{
    public string Root { get; } = playlistsDirectory;

    public List<PlaylistDocument> Load()
    {
        var list = new List<PlaylistDocument>();
        if (!Directory.Exists(Root)) return list;
        foreach (var path in Directory.EnumerateFiles(Root, "*" + PlaylistFile.Extension))
        {
            try
            {
                list.Add(new PlaylistDocument { FilePath = path, File = PlaylistFile.Load(path) });
            }
            catch (Exception ex)
            {
                list.Add(new PlaylistDocument { FilePath = path, LoadError = ex.Message });
            }
        }
        return list.OrderBy(d => d.File?.Name ?? Path.GetFileName(d.FilePath), StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Saves a playlist under its GUID-based file name and returns the path.</summary>
    public string Save(PlaylistFile playlist)
    {
        if (string.IsNullOrWhiteSpace(playlist.Name)) throw new InvalidOperationException("Playlist name can't be empty.");
        Directory.CreateDirectory(Root);
        string path = Path.Combine(Root, playlist.FileName);
        backups.BackupFile(path);
        playlist.Save(path);
        return path;
    }

    public void Delete(string path)
    {
        backups.BackupFile(path);
        if (File.Exists(path)) File.Delete(path);
    }
}
