namespace VolvoSplitter.Core;

/// <summary>
/// Standard-CRC32 (Polynom 0xEDB88320, Init 0xFFFFFFFF, Final-XOR 0xFFFFFFFF).
///
/// Das Originalprogramm rechnet byteweise mit einer nicht gespiegelten Tabelle
/// und spiegelt Ein- und Ausgabe selbst. Das Ergebnis ist bitidentisch mit
/// dieser Variante, nur um Größenordnungen langsamer.
/// </summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
