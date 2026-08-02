using VolvoSplitter.Core;

namespace VolvoSplitter.Core.Tests;

public class SRecordTests
{
    private sealed record Line(string Type, long Address, byte[] Data);

    [Fact]
    public void Write_RoundTripsDataAndAddresses()
    {
        var payload = new byte[100];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 7 + 3);
        const long baseAddress = 0x00108000;

        var lines = WriteAndParse(payload, baseAddress, "test");

        // Datensätze zusammensetzen und mit dem Original vergleichen.
        var s3 = lines.Where(l => l.Type == "S3").ToList();
        Assert.NotEmpty(s3);
        Assert.Equal(baseAddress, s3[0].Address);

        var reassembled = s3.SelectMany(l => l.Data).ToArray();
        Assert.Equal(payload, reassembled);

        // Adressen sind lückenlos aufsteigend um die jeweilige Zeilenlänge.
        long expected = baseAddress;
        foreach (var line in s3)
        {
            Assert.Equal(expected, line.Address);
            expected += line.Data.Length;
        }
    }

    [Fact]
    public void Write_EmitsHeaderAndTermination()
    {
        var lines = WriteAndParse(new byte[32], 0x1000, "hdr");
        Assert.Equal("S0", lines[0].Type);
        Assert.Equal("S7", lines[^1].Type);
    }

    [Fact]
    public void Write_EveryLineHasValidChecksum()
    {
        // ParseLine wirft bei falscher Prüfsumme — dass Parsen gelingt, prüft sie.
        var lines = WriteAndParse(new byte[500], 0x2000, "crc");
        Assert.All(lines, l => Assert.NotNull(l.Type));
    }

    private static List<Line> WriteAndParse(byte[] data, long baseAddress, string name)
    {
        string path = TestDump.TempPath(".s3");
        try
        {
            SRecord.Write(path, data, baseAddress, name);
            return File.ReadAllLines(path).Where(l => l.Length > 0).Select(ParseLine).ToList();
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Zerlegt eine S-Record-Zeile und prüft dabei das Prüfsummenbyte.</summary>
    private static Line ParseLine(string line)
    {
        string type = line[..2];
        int count = Convert.ToInt32(line.Substring(2, 2), 16);

        var bytes = new byte[count];
        for (int i = 0; i < count; i++)
            bytes[i] = Convert.ToByte(line.Substring(4 + i * 2, 2), 16);

        // Prüfsumme = Einerkomplement der Summe aus Count + allen Nutzbytes.
        int sum = count;
        for (int i = 0; i < count - 1; i++) sum += bytes[i];
        byte expected = (byte)(~sum & 0xFF);
        Assert.Equal(expected, bytes[^1]);

        // Adressbreite nach Typ: S0/S5 = 2, S3/S7 = 4 Byte.
        int addrLen = type is "S3" or "S7" ? 4 : 2;
        long address = 0;
        for (int i = 0; i < addrLen; i++) address = (address << 8) | bytes[i];

        var body = bytes[addrLen..^1];   // ohne Adresse und Prüfsumme
        return new Line(type, address, body);
    }
}
