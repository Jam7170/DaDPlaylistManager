namespace DaDPlaylistManager.Core;

/// <summary>Locations of the game's user data (the "Saved" folder under %LOCALAPPDATA%\Pagoda).</summary>
public sealed class GamePaths(string savedDirectory)
{
    public const string GameProcessName = "PagodaSteam-Win64-Shipping";

    public string Saved { get; } = savedDirectory;
    public string ImportedSongs => Path.Combine(Saved, "ImportedSongs");
    public string Playlists => Path.Combine(Saved, "Playlists");

    public static string DefaultSavedDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pagoda", "Saved");

    /// <summary>True if the folder looks like the game's Saved folder.</summary>
    public static bool LooksValid(string savedDirectory) =>
        Directory.Exists(savedDirectory) &&
        (Directory.Exists(Path.Combine(savedDirectory, "ImportedSongs")) ||
         Directory.Exists(Path.Combine(savedDirectory, "Playlists")) ||
         Directory.Exists(Path.Combine(savedDirectory, "SaveGames")));

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(ImportedSongs);
        Directory.CreateDirectory(Playlists);
    }

    public static bool IsGameRunning()
    {
        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName(GameProcessName);
            foreach (var p in procs) p.Dispose();
            return procs.Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
