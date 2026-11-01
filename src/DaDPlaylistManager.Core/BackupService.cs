namespace DaDPlaylistManager.Core;

/// <summary>
/// Copies game files aside before this tool changes them. Each app session gets one timestamped
/// folder, and each file is copied at most once per session, so the backup is the state from
/// before the session started editing it.
/// </summary>
public sealed class BackupService
{
    private readonly string _savedRoot;
    private readonly HashSet<string> _backedUp = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _keepSessions;
    private string? _sessionDir;

    public string Root { get; }

    public BackupService(string backupRoot, string savedRoot, int keepSessions = 30)
    {
        Root = backupRoot;
        _savedRoot = Path.GetFullPath(savedRoot);
        _keepSessions = keepSessions;
    }

    private string SessionDir
    {
        get
        {
            if (_sessionDir is null)
            {
                _sessionDir = Path.Combine(Root, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
                Directory.CreateDirectory(_sessionDir);
                Prune();
            }
            return _sessionDir;
        }
    }

    /// <summary>Backs up a file (if it exists and hasn't been backed up this session).</summary>
    public void BackupFile(string path)
    {
        string full = Path.GetFullPath(path);
        if (!File.Exists(full) || !_backedUp.Add(full)) return;
        string dest = Path.Combine(SessionDir, RelativeName(full));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(full, dest, overwrite: true);
    }

    /// <summary>Backs up every file in a directory except large audio, which is never modified in place.</summary>
    public void BackupSongMetadata(string songDirectory)
    {
        foreach (var f in Directory.EnumerateFiles(songDirectory, "*.json")) BackupFile(f);
    }

    private string RelativeName(string full)
    {
        if (full.StartsWith(_savedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return full[(_savedRoot.Length + 1)..];
        return Path.Combine("_other", Path.GetFileName(full));
    }

    private void Prune()
    {
        try
        {
            var dirs = new DirectoryInfo(Root).GetDirectories().OrderByDescending(d => d.Name).Skip(_keepSessions);
            foreach (var d in dirs) d.Delete(recursive: true);
        }
        catch
        {
            // Pruning is best effort.
        }
    }
}
