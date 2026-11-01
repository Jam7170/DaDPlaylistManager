using System.Diagnostics;
using System.Text;

namespace DaDPlaylistManager.Core.Audio;

/// <summary>Thin wrapper over an ffmpeg executable.</summary>
public sealed class Ffmpeg(string exePath)
{
    public string ExePath { get; } = exePath;

    /// <summary>Reads container tags (title, artist, ...) using ffmpeg's ffmetadata output.</summary>
    public async Task<Dictionary<string, string>> ReadTagsAsync(string input, CancellationToken ct = default)
    {
        var (code, stdout, _) = await RunTextAsync(["-v", "error", "-i", input, "-f", "ffmetadata", "-"], ct);
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (code != 0) return tags;
        foreach (var line in UnescapeLines(stdout))
        {
            if (line.StartsWith(';') || line.StartsWith('[')) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            tags.TryAdd(line[..eq].Trim(), line[(eq + 1)..].Trim());
        }
        return tags;
    }

    /// <summary>Converts any audio file to Ogg Vorbis stereo 44.1 kHz (~128 kbps, matching the game's own imports).</summary>
    public async Task ConvertToOggAsync(string input, string output, CancellationToken ct = default)
    {
        string[] common = ["-y", "-v", "error", "-i", input, "-vn", "-sn", "-dn", "-map_metadata", "-1", "-ac", "2", "-ar", "44100"];
        var (code, _, err) = await RunTextAsync([.. common, "-c:a", "libvorbis", "-q:a", "4", "-f", "ogg", output], ct);
        if (code != 0)
        {
            // Builds without libvorbis still have ffmpeg's native (experimental) encoder.
            var (code2, _, err2) = await RunTextAsync([.. common, "-c:a", "vorbis", "-strict", "experimental", "-b:a", "128k", "-f", "ogg", output], ct);
            if (code2 != 0) throw new InvalidOperationException("ffmpeg could not convert the file:\n" + (err.Length > 0 ? err : err2).Trim());
        }
    }

    /// <summary>Decodes to mono 32-bit float PCM at the given sample rate.</summary>
    public async Task<float[]> DecodeMonoAsync(string input, int sampleRate, CancellationToken ct = default)
    {
        var psi = CreateStartInfo(["-v", "error", "-i", input, "-vn", "-ac", "1", "-ar", sampleRate.ToString(), "-f", "f32le", "-"]);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        using var reg = ct.Register(() => TryKill(p));
        var errTask = p.StandardError.ReadToEndAsync(ct);
        using var ms = new MemoryStream();
        await p.StandardOutput.BaseStream.CopyToAsync(ms, ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new InvalidOperationException("ffmpeg could not decode the file:\n" + (await errTask).Trim());
        var bytes = ms.GetBuffer();
        var samples = new float[ms.Length / 4];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 4);
        return samples;
    }

    public async Task<bool> IsWorkingAsync()
    {
        try
        {
            var (code, _, _) = await RunTextAsync(["-version"], CancellationToken.None);
            return code == 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task<(int Code, string Stdout, string Stderr)> RunTextAsync(IEnumerable<string> args, CancellationToken ct)
    {
        using var p = Process.Start(CreateStartInfo(args)) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        using var reg = ct.Register(() => TryKill(p));
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, await outTask, await errTask);
    }

    private ProcessStartInfo CreateStartInfo(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(ExePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-hide_banner");
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }

    /// <summary>Splits ffmetadata text into logical lines, handling '\' escapes and continuations.</summary>
    private static IEnumerable<string> UnescapeLines(string text)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                char next = text[++i];
                if (next == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                sb.Append(next == '\r' ? '\n' : next);
            }
            else if (c == '\n')
            {
                yield return sb.ToString().TrimEnd('\r');
                sb.Clear();
            }
            else sb.Append(c);
        }
        if (sb.Length > 0) yield return sb.ToString().TrimEnd('\r');
    }
}
