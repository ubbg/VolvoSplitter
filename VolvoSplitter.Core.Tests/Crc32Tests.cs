using System.Text;
using VolvoSplitter.Core;

namespace VolvoSplitter.Core.Tests;

public class Crc32Tests
{
    [Fact]
    public void CheckVector_123456789_MatchesStandardCrc32()
    {
        // Der bekannte CRC-32/ISO-HDLC-Prüfwert der Zeichenkette "123456789".
        uint crc = Crc32.Compute(Encoding.ASCII.GetBytes("123456789"));
        Assert.Equal(0xCBF43926u, crc);
    }

    [Fact]
    public void EmptyInput_YieldsZero()
    {
        Assert.Equal(0u, Crc32.Compute(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void MatchesSlowReferenceImplementation()
    {
        var data = new byte[1024];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 31 + 7);

        Assert.Equal(SlowReference(data), Crc32.Compute(data));
    }

    /// <summary>Bit-für-Bit-Referenz ohne Tabelle — bewusst langsam, aber offensichtlich korrekt.</summary>
    private static uint SlowReference(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
