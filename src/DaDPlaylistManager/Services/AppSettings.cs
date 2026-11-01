using System.IO;
using System.Text.Json;

namespace DaDPlaylistManager.Services;

/// <summary>User settings, stored as JSON in %LOCALAPPDATA%\DaDPlaylistManager.</summary>
public sealed class AppSettings
{
    public static string AppDataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DaDPlaylistManager");

    private static string FilePath => Path.Combine(AppDataDirectory, "settings.json");

    public string? SavedDirectory { get; set; }
    public string? FfmpegPath { get; set; }
    public double Volume { get; set; } = 0.7;
    public bool Metronome { get; set; }
    public bool SyncFolderNames { get; set; } = true;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // Fall back to defaults if the file is damaged.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppDataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Settings are a convenience; never crash over them.
        }
    }
}
