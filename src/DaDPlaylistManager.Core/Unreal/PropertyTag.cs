namespace DaDPlaylistManager.Core.Unreal;

/// <summary>
/// A property type name as serialized by UE 5.4+ (FPropertyTypeName): a name followed by
/// a count of nested parameter type names, e.g. ArrayProperty(ObjectProperty) or
/// StructProperty(Guid(/Script/CoreUObject)).
/// </summary>
public sealed class PropertyType
{
    public string Name { get; }
    public IReadOnlyList<PropertyType> Parameters { get; }

    public PropertyType(string name, params PropertyType[] parameters)
    {
        Name = name;
        Parameters = parameters;
    }

    public static PropertyType Read(BinaryReader r)
    {
        string name = r.ReadFString();
        int count = r.ReadInt32();
        if (count is < 0 or > 16) throw new InvalidDataException($"Bad type parameter count {count} for {name}.");
        var ps = new PropertyType[count];
        for (int i = 0; i < count; i++) ps[i] = Read(r);
        return new PropertyType(name, ps);
    }

    public void Write(BinaryWriter w)
    {
        w.WriteFString(Name);
        w.Write(Parameters.Count);
        foreach (var p in Parameters) p.Write(w);
    }

    public override string ToString() =>
        Parameters.Count == 0 ? Name : $"{Name}({string.Join(",", Parameters)})";
}

[Flags]
public enum PropertyTagFlags : byte
{
    None = 0x00,
    HasArrayIndex = 0x01,
    HasPropertyGuid = 0x02,
    HasPropertyExtensions = 0x04,
    HasBinaryOrNativeSerialize = 0x08,
    BoolTrue = 0x10,
    SkippedSerialize = 0x20,
}

/// <summary>
/// One tagged property from a UE 5.4+ tagged-property stream. The value is kept as raw bytes
/// so properties we don't understand survive a round trip untouched.
/// </summary>
public sealed class PropertyTag
{
    public required string Name { get; init; }
    public required PropertyType Type { get; init; }
    public PropertyTagFlags Flags { get; set; }
    public int ArrayIndex { get; set; }
    public byte[]? PropertyGuid { get; set; }
    public byte[] Value { get; set; } = [];

    public const string Terminator = "None";

    /// <summary>Reads the next tag, or returns null at the "None" terminator.</summary>
    public static PropertyTag? Read(BinaryReader r)
    {
        string name = r.ReadFString();
        if (name == Terminator) return null;

        var type = PropertyType.Read(r);
        int size = r.ReadInt32();
        var flags = (PropertyTagFlags)r.ReadByte();
        int arrayIndex = 0;
        byte[]? guid = null;
        if (flags.HasFlag(PropertyTagFlags.HasArrayIndex)) arrayIndex = r.ReadInt32();
        if (flags.HasFlag(PropertyTagFlags.HasPropertyGuid)) guid = r.ReadBytes(16);
        if (flags.HasFlag(PropertyTagFlags.HasPropertyExtensions))
            throw new NotSupportedException($"Property '{name}' uses property extensions, which this tool does not support.");
        if (size < 0) throw new InvalidDataException($"Negative size for property '{name}'.");

        byte[] value = r.ReadBytes(size);
        if (value.Length != size) throw new EndOfStreamException();
        return new PropertyTag { Name = name, Type = type, Flags = flags, ArrayIndex = arrayIndex, PropertyGuid = guid, Value = value };
    }

    public void Write(BinaryWriter w)
    {
        w.WriteFString(Name);
        Type.Write(w);
        w.Write(Value.Length);
        var flags = Flags;
        if (PropertyGuid is null) flags &= ~PropertyTagFlags.HasPropertyGuid;
        else flags |= PropertyTagFlags.HasPropertyGuid;
        w.Write((byte)flags);
        if (flags.HasFlag(PropertyTagFlags.HasArrayIndex)) w.Write(ArrayIndex);
        if (PropertyGuid is not null) w.Write(PropertyGuid);
        w.Write(Value);
    }

    public static void WriteTerminator(BinaryWriter w) => w.WriteFString(Terminator);
}
