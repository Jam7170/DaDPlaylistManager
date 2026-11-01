using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DaDPlaylistManager.Core.Songs;

namespace DaDPlaylistManager.Core.Sync;

/// <summary>
/// A short fingerprint of a playlist's songs and their timing, so players can check they have
/// identical setups. Must match the server's implementation exactly:
/// one line per song "hash|tempo|beatOffset|startSongOffset|endSongOffset|seed|sections",
/// sections = "tempo@start" joined by ",", lines joined by "\n", SHA-256, first 8 hex chars, "XXXX-XXXX".
/// Numbers are formatted like JavaScript's String(Number(x)).
/// </summary>
public static class SetCode
{
    public static string Compute(IEnumerable<(string Hash, SongMeta Meta)> songs) =>
        ComputeFromLines(songs.Select(s => Line(s.Hash, s.Meta)));

    public static string Line(string hash, SongMeta m)
    {
        string sections = string.Join(",", m.CustomTempoSections.Select(s => $"{JsNumber(s.Tempo)}@{JsNumber(s.StartAbsoluteTime)}"));
        return $"{hash}|{JsNumber(m.Tempo)}|{JsNumber(m.BeatOffsetRaw)}|{JsNumber(m.StartSongOffset)}|{JsNumber(m.EndSongOffset)}|{JsNumber(m.Seed)}|{sections}";
    }

    public static string ComputeFromLines(IEnumerable<string> lines)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines)));
        string hex = Convert.ToHexString(bytes)[..8];
        return $"{hex[..4]}-{hex[4..]}";
    }

    /// <summary>Formats a double the way JavaScript's Number.prototype.toString does.</summary>
    public static string JsNumber(double v)
    {
        if (double.IsNaN(v)) return "NaN";
        if (double.IsPositiveInfinity(v)) return "Infinity";
        if (double.IsNegativeInfinity(v)) return "-Infinity";
        if (v == 0) return "0";

        // "R" gives the shortest round-trippable digits; re-lay them out with JS's rules.
        string shortest = v.ToString("R", CultureInfo.InvariantCulture);
        bool negative = shortest.StartsWith('-');
        string s = negative ? shortest[1..] : shortest;
        int ePos = s.IndexOfAny(['E', 'e']);
        string mantissa = ePos >= 0 ? s[..ePos] : s;
        int expPart = ePos >= 0 ? int.Parse(s[(ePos + 1)..], CultureInfo.InvariantCulture) : 0;
        int dot = mantissa.IndexOf('.');
        string intPart = dot >= 0 ? mantissa[..dot] : mantissa;
        string fracPart = dot >= 0 ? mantissa[(dot + 1)..] : "";
        string all = intPart + fracPart;
        string digits = all.TrimStart('0');
        // n = position of the decimal point relative to the first significant digit (the spec's "n").
        int n = intPart.Length + expPart - (all.Length - digits.Length);
        digits = digits.TrimEnd('0');
        if (digits.Length == 0) return "0";

        int k = digits.Length;
        string result;
        if (k <= n && n <= 21)
            result = digits + new string('0', n - k);
        else if (0 < n && n <= 21)
            result = digits[..n] + "." + digits[n..];
        else if (-6 < n && n <= 0)
            result = "0." + new string('0', -n) + digits;
        else
        {
            int e = n - 1;
            string sign = e < 0 ? "-" : "+";
            result = (k == 1 ? digits : digits[0] + "." + digits[1..]) + "e" + sign + Math.Abs(e).ToString(CultureInfo.InvariantCulture);
        }
        return negative ? "-" + result : result;
    }
}
