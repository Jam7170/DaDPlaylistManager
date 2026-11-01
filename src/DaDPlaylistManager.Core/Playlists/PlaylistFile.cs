using System.Security.Cryptography;
using DaDPlaylistManager.Core.Unreal;

namespace DaDPlaylistManager.Core.Playlists;

/// <summary>
/// One entry in a playlist's Songs array. Imported songs are identified by their
/// <c>uniqueId</c> from Meta.json; anything else (e.g. a built-in game song) is kept as its
/// raw object path so it round-trips untouched.
/// </summary>
public sealed record PlaylistEntry(string ObjectPath, uint? ImportedSongId);

/// <summary>
/// Reader/writer for Dead as Disco <c>.bjpl</c> playlist files (Saved/Playlists).
/// </summary>
/// <remarks>
/// Layout (EPagodaPlaylistVersion::CustomSerializeImportedSongs = 2):
/// <code>
/// int32  version
/// uint8  class serialization control extension (0)
/// tagged properties until "None":
///   UniqueID       StructProperty(Guid)   - also the file name: Playlist_{GUID}.bjpl
///   Name           StrProperty
///   bShouldShuffle BoolProperty
///   Songs          ArrayProperty(ObjectProperty) - object paths as FStrings
/// uint32 0         (no object GUID)
/// int32  count, then count x { uint32 importedSongUniqueId, int32 indexIntoSongs }
/// </code>
/// The object paths for imported songs point at transient objects that only exist while the game
/// runs, so the game resolves imported songs through the trailing id/index table instead.
/// </remarks>
public sealed class PlaylistFile
{
    public const int LatestVersion = 2;
    public const string Extension = ".bjpl";
    public const string FilePrefix = "Playlist_";

    /// <summary>Default prefix for imported-song object paths, as written by the game.</summary>
    public const string DefaultTransientPrefix =
        "/Engine/Transient.GameEngine_2147482571:GI_PagodaGameInstance_C_2147482508.PagodaSongCatalogSubsystem_2147482331";

    private const string UniqueIdProp = "UniqueID";
    private const string NameProp = "Name";
    private const string ShuffleProp = "bShouldShuffle";
    private const string SongsProp = "Songs";

    public int Version { get; private set; } = LatestVersion;

    /// <summary>The four 32-bit words of the playlist's FGuid (A, B, C, D).</summary>
    public uint[] Guid { get; set; } = NewGuid();

    public string Name { get; set; } = "New Playlist";
    public bool Shuffle { get; set; }
    public List<PlaylistEntry> Entries { get; } = [];

    /// <summary>Tagged properties in file order, including ones this tool doesn't interpret.</summary>
    private readonly List<PropertyTag> _tags = [];
    private byte _serializationControl;
    private uint _objectGuidFlag;
    private byte[] _trailingBytes = [];

    public string GuidHex => string.Concat(Guid.Select(g => g.ToString("X8")));
    public string FileName => FilePrefix + GuidHex + Extension;

    public static uint[] NewGuid()
    {
        Span<byte> b = stackalloc byte[16];
        RandomNumberGenerator.Fill(b);
        return [BitConverter.ToUInt32(b[..4]), BitConverter.ToUInt32(b[4..8]), BitConverter.ToUInt32(b[8..12]), BitConverter.ToUInt32(b[12..])];
    }

    public static PlaylistFile Load(string path) => Parse(File.ReadAllBytes(path));

    public static PlaylistFile Parse(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);
        var pl = new PlaylistFile { Version = r.ReadInt32() };
        if (pl.Version is < 1 or > LatestVersion)
            throw new InvalidDataException($"Unsupported playlist version {pl.Version}.");
        pl._serializationControl = r.ReadByte();
        if (pl._serializationControl != 0)
            throw new NotSupportedException($"Unsupported serialization control flags {pl._serializationControl}.");

        while (PropertyTag.Read(r) is { } tag) pl._tags.Add(tag);

        // Interpret the properties we know.
        var paths = new List<string>();
        foreach (var tag in pl._tags)
        {
            switch (tag.Name)
            {
                case UniqueIdProp when tag.Value.Length == 16:
                    pl.Guid = [BitConverter.ToUInt32(tag.Value, 0), BitConverter.ToUInt32(tag.Value, 4), BitConverter.ToUInt32(tag.Value, 8), BitConverter.ToUInt32(tag.Value, 12)];
                    break;
                case NameProp:
                    using (var vr = new BinaryReader(new MemoryStream(tag.Value))) pl.Name = vr.ReadFString();
                    break;
                case ShuffleProp:
                    pl.Shuffle = tag.Flags.HasFlag(PropertyTagFlags.BoolTrue);
                    break;
                case SongsProp:
                    using (var vr = new BinaryReader(new MemoryStream(tag.Value)))
                    {
                        int n = vr.ReadInt32();
                        for (int i = 0; i < n; i++) paths.Add(vr.ReadFString());
                    }
                    break;
            }
        }

        var ids = new uint?[paths.Count];
        long remaining = ms.Length - ms.Position;
        if (remaining >= 4)
        {
            pl._objectGuidFlag = r.ReadUInt32();
            remaining -= 4;
        }
        if (pl.Version >= 2 && remaining >= 4)
        {
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                uint id = r.ReadUInt32();
                int index = r.ReadInt32();
                if (index >= 0 && index < ids.Length) ids[index] = id;
            }
        }
        pl._trailingBytes = r.ReadBytes((int)(ms.Length - ms.Position));

        for (int i = 0; i < paths.Count; i++) pl.Entries.Add(new PlaylistEntry(paths[i], ids[i]));
        return pl;
    }

    /// <summary>Returns the transient path prefix used by this file's imported-song entries.</summary>
    public string TransientPrefix
    {
        get
        {
            foreach (var e in Entries)
            {
                if (e.ImportedSongId is null) continue;
                int marker = e.ObjectPath.IndexOf(".PagodaSongCatalogSubsystem_", StringComparison.Ordinal);
                if (marker < 0) continue;
                int end = e.ObjectPath.IndexOf('.', marker + 1);
                if (end > 0) return e.ObjectPath[..end];
            }
            return DefaultTransientPrefix;
        }
    }

    public static string ImportedObjectPath(string prefix, string folderName) => prefix + "." + folderName;

    /// <summary>Folder name an imported-song entry's object path refers to, if any.</summary>
    public static string? FolderNameFromPath(string objectPath)
    {
        int marker = objectPath.IndexOf(".PagodaSongCatalogSubsystem_", StringComparison.Ordinal);
        if (marker < 0) return null;
        int dot = objectPath.IndexOf('.', marker + 1);
        return dot < 0 ? null : objectPath[(dot + 1)..];
    }

    public byte[] Serialize()
    {
        var tags = BuildTags();
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(LatestVersion);
            w.Write(_serializationControl);
            foreach (var t in tags) t.Write(w);
            PropertyTag.WriteTerminator(w);
            w.Write(_objectGuidFlag);

            var imported = Entries.Select((e, i) => (e, i)).Where(x => x.e.ImportedSongId is not null).ToList();
            w.Write(imported.Count);
            foreach (var (e, i) in imported)
            {
                w.Write(e.ImportedSongId!.Value);
                w.Write(i);
            }
            w.Write(_trailingBytes);
        }
        return ms.ToArray();
    }

    public void Save(string path)
    {
        var bytes = Serialize();
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    private List<PropertyTag> BuildTags()
    {
        var result = new List<PropertyTag>(_tags);
        Upsert(result, UniqueIdProp, () => new PropertyTag
        {
            Name = UniqueIdProp,
            Type = new PropertyType("StructProperty", new PropertyType("Guid", new PropertyType("/Script/CoreUObject"))),
            Flags = PropertyTagFlags.HasBinaryOrNativeSerialize,
        }).Value = Guid.SelectMany(BitConverter.GetBytes).ToArray();

        Upsert(result, NameProp, () => new PropertyTag { Name = NameProp, Type = new PropertyType("StrProperty") })
            .Value = Write(w => w.WriteFString(Name));

        // Unreal omits properties at their default value, so the game writes bShouldShuffle only when true.
        if (Shuffle)
        {
            var shuffle = Upsert(result, ShuffleProp, () => new PropertyTag { Name = ShuffleProp, Type = new PropertyType("BoolProperty") }, insertBefore: SongsProp);
            shuffle.Value = [];
            shuffle.Flags |= PropertyTagFlags.BoolTrue;
        }
        else
        {
            result.RemoveAll(t => t.Name == ShuffleProp);
        }

        Upsert(result, SongsProp, () => new PropertyTag
        {
            Name = SongsProp,
            Type = new PropertyType("ArrayProperty", new PropertyType("ObjectProperty")),
        }).Value = Write(w =>
        {
            w.Write(Entries.Count);
            foreach (var e in Entries) w.WriteFString(e.ObjectPath);
        });
        return result;
    }

    private static PropertyTag Upsert(List<PropertyTag> tags, string name, Func<PropertyTag> create, string? insertBefore = null)
    {
        int i = tags.FindIndex(t => t.Name == name);
        if (i >= 0)
        {
            // Clone so the parsed originals stay untouched until the next load.
            var old = tags[i];
            tags[i] = new PropertyTag { Name = old.Name, Type = old.Type, Flags = old.Flags, ArrayIndex = old.ArrayIndex, PropertyGuid = old.PropertyGuid, Value = old.Value };
            return tags[i];
        }
        var tag = create();
        int before = insertBefore is null ? -1 : tags.FindIndex(t => t.Name == insertBefore);
        if (before >= 0) tags.Insert(before, tag);
        else tags.Add(tag);
        return tag;
    }

    private static byte[] Write(Action<BinaryWriter> body)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms)) body(w);
        return ms.ToArray();
    }

    /// <summary>Makes a copy with a fresh GUID (for "Duplicate" and pack imports).</summary>
    public PlaylistFile CloneAsNew(string name)
    {
        var copy = Parse(Serialize());
        copy.Guid = NewGuid();
        copy.Name = name;
        return copy;
    }
}
