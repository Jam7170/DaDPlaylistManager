using System.IO;
using System.Reflection;

namespace DaDPlaylistManager.Services;

/// <summary>
/// Finds an ffmpeg executable: a configured path, a copy next to the app, the copy embedded in
/// release builds (extracted on first use), or one on PATH.
/// </summary>
public static class FfmpegLocator
{
    private const string EmbeddedName = "ffmpeg.exe";

    public static string? Find(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath)) return configuredPath;

        string baseDir = AppContext.BaseDirectory;
        foreach (var candidate in new[] { Path.Combine(baseDir, "ffmpeg.exe"), Path.Combine(baseDir, "tools", "ffmpeg.exe") })
            if (File.Exists(candidate)) return candidate;

        if (ExtractEmbedded() is { } extracted) return extracted;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string p = Path.Combine(dir.Trim('"'), "ffmpeg.exe");
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException)
            {
                // Ignore malformed PATH entries.
            }
        }
        return null;
    }

    public static bool HasEmbedded => typeof(FfmpegLocator).Assembly.GetManifestResourceInfo(EmbeddedName) is not null;

    private static string? ExtractEmbedded()
    {
        var asm = typeof(FfmpegLocator).Assembly;
        using var stream = asm.GetManifestResourceStream(EmbeddedName);
        if (stream is null) return null;

        string version = asm.GetName().Version?.ToString() ?? "0";
        string dir = Path.Combine(AppSettings.AppDataDirectory, "tools");
        string target = Path.Combine(dir, $"ffmpeg-{version}-{stream.Length}.exe");
        if (File.Exists(target) && new FileInfo(target).Length == stream.Length) return target;

        Directory.CreateDirectory(dir);
        foreach (var old in Directory.EnumerateFiles(dir, "ffmpeg-*.exe"))
        {
            try { File.Delete(old); } catch { }
        }
        string tmp = target + ".tmp";
        using (var fs = File.Create(tmp)) stream.CopyTo(fs);
        File.Move(tmp, target, overwrite: true);
        return target;
    }
}
