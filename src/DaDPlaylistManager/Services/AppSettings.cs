using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    // Shared playlists (synced through the group's website).
    public string SyncServer { get; set; } = "https://heroicgames.uk";
    /// <summary>The site's session token, encrypted for the current Windows user (DPAPI).</summary>
    public string? SyncTokenProtected { get; set; }
    public int? SyncPlayerId { get; set; }
    public string? SyncPlayerName { get; set; }
    public bool AutoSyncShared { get; set; } = true;
    /// <summary>"server#playlistId" → GUID of the local game playlist written for it.</summary>
    public Dictionary<string, string> SharedPlaylistLinks { get; set; } = [];

    [JsonIgnore]
    public string? SyncToken
    {
        get
        {
            if (SyncTokenProtected is null) return null;
            try
            {
                var bytes = System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(SyncTokenProtected), null,
                    System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return null;
            }
        }
        set => SyncTokenProtected = value is null
            ? null
            : Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(value), null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser));
    }

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
