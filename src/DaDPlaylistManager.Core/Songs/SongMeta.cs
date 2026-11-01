using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DaDPlaylistManager.Core.Songs;

public sealed record TempoSection(double Tempo, double StartAbsoluteTime);

/// <summary>
/// Typed view over an imported song's Meta.json. The underlying JSON object is kept so that
/// fields added by future game versions are preserved when saving.
/// </summary>
public sealed class SongMeta
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\r\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly JsonObject _json;

    private SongMeta(JsonObject json) => _json = json;

    public static SongMeta Parse(string json)
    {
        var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        return new SongMeta(node as JsonObject ?? throw new InvalidDataException("Meta.json is not a JSON object."));
    }

    public static SongMeta Load(string path) => Parse(File.ReadAllText(path));

    public static SongMeta CreateNew(uint uniqueId, string songName, IEnumerable<string> performedBy, double tempo, int beatOffsetMs)
    {
        var json = new JsonObject
        {
            ["version"] = CurrentVersion,
            ["uniqueId"] = uniqueId,
            ["songName"] = songName,
            ["performedBy"] = new JsonArray(),
            ["writtenBy"] = new JsonArray(),
            ["seed"] = RandomUInt(),
            ["tempo"] = tempo,
            ["customTempoSections"] = new JsonArray(),
            ["beatOffset"] = beatOffsetMs,
            ["startSongOffset"] = 0,
            ["endSongOffset"] = 0,
            ["originalAudioFileHash"] = "",
            ["originalAudioFilePath"] = "",
        };
        var meta = new SongMeta(json) { PerformedBy = performedBy.ToList() };
        return meta;
    }

    public SongMeta Clone() => Parse(ToJson());

    /// <summary>The JSON object as a compact string (for sending to the sync server).</summary>
    public string ToCompactJson() => _json.ToJsonString();

    /// <summary>
    /// Copies everything from <paramref name="source"/> except this machine's identity fields
    /// (uniqueId, original audio path and hash), so shared songs play identically everywhere.
    /// </summary>
    public void AdoptFrom(SongMeta source)
    {
        uint id = UniqueId;
        string path = OriginalAudioFilePath, hash = OriginalAudioFileHash;
        foreach (var key in _json.Select(kv => kv.Key).ToList()) _json.Remove(key);
        foreach (var (key, value) in source._json) _json[key] = value?.DeepClone();
        UniqueId = id;
        OriginalAudioFilePath = path;
        OriginalAudioFileHash = hash;
    }

    /// <summary>True when the fields that affect gameplay or display match.</summary>
    public bool SharedFieldsEqual(SongMeta other)
    {
        static JsonObject Strip(SongMeta m)
        {
            var c = (JsonObject)m._json.DeepClone();
            c.Remove("uniqueId");
            c.Remove("originalAudioFilePath");
            c.Remove("originalAudioFileHash");
            return c;
        }
        return JsonNode.DeepEquals(Strip(this), Strip(other));
    }

    public string ToJson() => _json.ToJsonString(WriteOptions);

    public void Save(string path)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, ToJson());
        File.Move(tmp, path, overwrite: true);
    }

    public static uint RandomUInt() => BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));

    public int Version => GetInt("version") ?? 0;

    public uint UniqueId
    {
        get => GetUInt("uniqueId");
        set => _json["uniqueId"] = value;
    }

    public string SongName
    {
        get => GetString("songName");
        set => _json["songName"] = value;
    }

    public List<string> PerformedBy
    {
        get => GetStringList("performedBy");
        set => _json["performedBy"] = new JsonArray(value.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
    }

    public List<string> WrittenBy
    {
        get => GetStringList("writtenBy");
        set => _json["writtenBy"] = new JsonArray(value.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
    }

    public uint Seed
    {
        get => GetUInt("seed");
        set => _json["seed"] = value;
    }

    public double Tempo
    {
        get => GetDouble("tempo");
        set => _json["tempo"] = value;
    }

    /// <summary>Position of the first beat, in milliseconds.</summary>
    public int BeatOffset
    {
        get => (int)Math.Round(GetDouble("beatOffset"));
        set => _json["beatOffset"] = value;
    }

    /// <summary>Beat offset exactly as stored (the set code uses it unrounded).</summary>
    public double BeatOffsetRaw => GetDouble("beatOffset");

    /// <summary>Seconds trimmed from the start of the audio.</summary>
    public double StartSongOffset
    {
        get => GetDouble("startSongOffset");
        set => _json["startSongOffset"] = value;
    }

    /// <summary>Seconds trimmed from the end of the audio.</summary>
    public double EndSongOffset
    {
        get => GetDouble("endSongOffset");
        set => _json["endSongOffset"] = value;
    }

    public List<TempoSection> CustomTempoSections
    {
        get
        {
            var list = new List<TempoSection>();
            if (_json["customTempoSections"] is JsonArray arr)
            {
                foreach (var item in arr.OfType<JsonObject>())
                    list.Add(new TempoSection(ToDouble(item["tempo"]), ToDouble(item["startAbsoluteTime"])));
            }
            return list;
        }
        set => _json["customTempoSections"] = new JsonArray(value
            .Select(s => (JsonNode?)new JsonObject { ["tempo"] = s.Tempo, ["startAbsoluteTime"] = s.StartAbsoluteTime })
            .ToArray());
    }

    public string OriginalAudioFileHash
    {
        get => GetString("originalAudioFileHash");
        set => _json["originalAudioFileHash"] = value;
    }

    public string OriginalAudioFilePath
    {
        get => GetString("originalAudioFilePath");
        set => _json["originalAudioFilePath"] = value;
    }

    public string ArtistsDisplay => string.Join(", ", PerformedBy);

    private string GetString(string key) => _json[key] is JsonValue v && v.TryGetValue(out string? s) ? s : string.Empty;

    private List<string> GetStringList(string key) =>
        _json[key] is JsonArray arr
            ? arr.Select(n => n is JsonValue v && v.TryGetValue(out string? s) ? s : n?.ToString() ?? "").ToList()
            : [];

    private double GetDouble(string key) => ToDouble(_json[key]);

    private int? GetInt(string key) => TryNumber(_json[key], out double d) ? (int)d : null;

    private uint GetUInt(string key)
    {
        if (!TryNumber(_json[key], out double d)) return 0;
        return d is >= 0 and <= uint.MaxValue ? (uint)d : unchecked((uint)(long)d);
    }

    private static double ToDouble(JsonNode? node) => TryNumber(node, out double d) ? d : 0;

    // Values parsed from disk and values assigned in code (int/uint/double) are stored differently
    // by JsonValue, so go through the JSON text to read any of them uniformly.
    private static bool TryNumber(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue v) return false;
        string text = v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString();
        return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
    }
}
