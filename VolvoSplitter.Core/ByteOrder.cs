namespace VolvoSplitter.Core;

/// <summary>Bytereihenfolge eines Abbilds.</summary>
public enum Endianness
{
    /// <summary>PowerPC — MPC5674F, MPC5777C.</summary>
    Big,

    /// <summary>Infineon TriCore.</summary>
    Little
}

/// <summary>
/// Wortzugriffe mit ausdrücklicher Bytereihenfolge. Bis Version 1 war
/// „big endian" im ganzen Kern fest verdrahtet; TriCore-Abbilder sind
/// little endian, deshalb steht die Reihenfolge jetzt an jedem Zugriff.
/// </summary>
public static class ByteOrder
{
    public static uint ReadUInt32(ReadOnlySpan<byte> data, long offset, Endianness order)
    {
        int at = (int)offset;
        return order == Endianness.Big
            ? (uint)((data[at] << 24) | (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3])
            : (uint)(data[at] | (data[at + 1] << 8) | (data[at + 2] << 16) | (data[at + 3] << 24));
    }

    public static ushort ReadUInt16(ReadOnlySpan<byte> data, long offset, Endianness order)
    {
        int at = (int)offset;
        return order == Endianness.Big
            ? (ushort)((data[at] << 8) | data[at + 1])
            : (ushort)(data[at] | (data[at + 1] << 8));
    }

    public static void WriteUInt32(Span<byte> data, long offset, uint value, Endianness order)
    {
        int at = (int)offset;
        if (order == Endianness.Big)
        {
            data[at] = (byte)(value >> 24);
            data[at + 1] = (byte)(value >> 16);
            data[at + 2] = (byte)(value >> 8);
            data[at + 3] = (byte)value;
        }
        else
        {
            data[at] = (byte)value;
            data[at + 1] = (byte)(value >> 8);
            data[at + 2] = (byte)(value >> 16);
            data[at + 3] = (byte)(value >> 24);
        }
    }

    /// <summary>Die vier Bytes eines Wertes in der gewünschten Reihenfolge.</summary>
    public static byte[] Pattern(uint value, Endianness order)
    {
        var pattern = new byte[4];
        WriteUInt32(pattern, 0, value, order);
        return pattern;
    }

    /// <summary>Erste Fundstelle eines 32-Bit-Werts im Fenster, oder -1.</summary>
    public static int IndexOfUInt32(ReadOnlySpan<byte> window, uint value, Endianness order)
    {
        Span<byte> pattern = stackalloc byte[4];
        WriteUInt32(pattern, 0, value, order);
        return window.IndexOf(pattern);
    }
}
