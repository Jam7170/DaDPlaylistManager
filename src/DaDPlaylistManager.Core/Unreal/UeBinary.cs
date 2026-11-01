using System.Text;

namespace DaDPlaylistManager.Core.Unreal;

/// <summary>Helpers for the Unreal Engine binary primitives used in SaveGame-style archives.</summary>
public static class UeBinary
{
    /// <summary>
    /// Reads an FString: int32 length (including the null terminator), positive for
    /// single-byte characters, negative for UTF-16.
    /// </summary>
    public static string ReadFString(this BinaryReader r)
    {
        int len = r.ReadInt32();
        if (len == 0) return string.Empty;
        if (len > 0)
        {
            if (len > 1_000_000) throw new InvalidDataException($"FString length {len} is implausible.");
            byte[] bytes = r.ReadBytes(len);
            if (bytes.Length != len) throw new EndOfStreamException();
            return Encoding.Latin1.GetString(bytes, 0, len - 1);
        }
        int chars = -len;
        if (chars > 1_000_000) throw new InvalidDataException($"FString length {len} is implausible.");
        byte[] wide = r.ReadBytes(chars * 2);
        if (wide.Length != chars * 2) throw new EndOfStreamException();
        return Encoding.Unicode.GetString(wide, 0, (chars - 1) * 2);
    }

    public static void WriteFString(this BinaryWriter w, string value)
    {
        if (value.Length == 0)
        {
            w.Write(0);
            return;
        }
        bool ansi = value.All(c => c < 0x80);
        if (ansi)
        {
            w.Write(value.Length + 1);
            w.Write(Encoding.ASCII.GetBytes(value));
            w.Write((byte)0);
        }
        else
        {
            w.Write(-(value.Length + 1));
            w.Write(Encoding.Unicode.GetBytes(value));
            w.Write((short)0);
        }
    }

    /// <summary>Byte size of an FString as written by <see cref="WriteFString"/>.</summary>
    public static int FStringSize(string value)
    {
        if (value.Length == 0) return 4;
        return value.All(c => c < 0x80) ? 4 + value.Length + 1 : 4 + (value.Length + 1) * 2;
    }
}
