using VolvoSplitter.Core;
using VolvoSplitter.Core.TriCore;

namespace VolvoSplitter.Core.Tests;

public class BoschChecksumTests
{
    private static byte[] Body(int length, uint seed)
    {
        var data = new byte[length];
        uint s = seed | 1;
        for (int i = 0; i < length; i++)
        {
            s = s * 1664525 + 1013904223;
            data[i] = (byte)(s >> 24);
        }
        return data;
    }

    [Fact]
    public void Crc32Residue_IsTheOnesComplementOfTheExpectedValue()
    {
        // 0x35015001 ist kein eigener Sollwert, sondern die Folge daraus, dass
        // die CRC32 ohne Schlussabgleich läuft.
        Assert.Equal(BoschChecksum.Crc32Residue, ~BoschChecksum.DefaultExpectedValue);
        Assert.Equal(0x35015001u, ~0xCAFEAFFEu);
    }

    [Fact]
    public void Crc32Algo_MatchesExpectedValue()
    {
        // Bereich mit gestelltem Schlusswort: die CRC32 mit Startwert
        // 0xFADECAFE muss auf dem Restwert 0x35015001 stehen bleiben.
        var body = Body(0x400, 0xC0FFEE);

        uint prefix = BoschChecksum.Crc32(body.AsSpan(0, body.Length - 4),
                                          BoschChecksum.DefaultStartValue);
        TestDump.WriteLe(body, body.Length - 4,
                         prefix ^ TriCoreDump.CrcAdjust(BoschChecksum.Crc32Residue));

        Assert.Equal(BoschChecksum.Crc32Residue,
                     BoschChecksum.Crc32(body, BoschChecksum.DefaultStartValue));
    }

    [Fact]
    public void Crc32Table_ComputesExactlyWhatTheBitwiseDefinitionDoes()
    {
        // Die bitweise Fassung ist maßgeblich. Die Tabellenvariante wird nur
        // benutzt, solange sie zeichengenau dasselbe rechnet.
        foreach (int length in new[] { 0, 1, 3, 4, 17, 256, 4095 })
        {
            var body = Body(length, (uint)(length * 2654435761));
            Assert.Equal(BoschChecksum.Crc32Bitwise(body, BoschChecksum.DefaultStartValue),
                         BoschChecksum.Crc32(body, BoschChecksum.DefaultStartValue));
        }
    }

    [Fact]
    public void Add32_MatchesExpectedValue()
    {
        // Ein einziges Doppelwort am Bereichsende genügt: es kann die Summe um
        // jeden 32-Bit-Betrag verschieben.
        var body = Body(0x400, 0xBEEF);

        TestDump.WriteLe(body, body.Length - 4,
            BoschChecksum.DefaultExpectedValue -
            BoschChecksum.Add32(body.AsSpan(0, body.Length - 4), BoschChecksum.DefaultStartValue));

        Assert.Equal(BoschChecksum.DefaultExpectedValue,
                     BoschChecksum.Add32(body, BoschChecksum.DefaultStartValue));
    }

    [Fact]
    public void Add16_MatchesExpectedValue()
    {
        // Das letzte Wort zählt um 16 Bit geschoben, das vorletzte normal —
        // zusammen decken sie jeden 32-Bit-Abstand ab.
        var body = Body(TriCoreDump.Add16AdjustBytes + 0x400, 0xBEEF);
        TriCoreDump.AdjustAdd16(body, 0, body.Length);

        Assert.Equal(BoschChecksum.DefaultExpectedValue,
                     BoschChecksum.Add16(body, BoschChecksum.DefaultStartValue));
    }

    [Fact]
    public void UnknownAlgorithmId_IsReportedNotGuessed()
    {
        // Eine Kennung außerhalb {0x00, 0x01, 0x10} führt zu „unbekannt" —
        // nicht zu einem geratenen Verfahren.
        Assert.False(BoschChecksum.IsKnown(0x02));
        Assert.Null(BoschChecksum.Compute(0x02, Body(0x40, 1), BoschChecksum.DefaultStartValue));
        Assert.Null(BoschChecksum.Expected(0x02, BoschChecksum.DefaultExpectedValue));
        Assert.Contains("unbekannt", BoschChecksum.Name(0x02));
    }

    [Fact]
    public void KnownAlgorithms_CarryTheNamesFromTheSource()
    {
        // Die Bezeichner stammen aus den ausgewerteten Werkzeugen, nicht aus
        // einer Bosch-Unterlage — sie werden übernommen, aber nicht erfunden.
        Assert.Equal("SB_CRC32_ALGO_E", BoschChecksum.Name(0x00));
        Assert.Equal("SB_ADD32_ALGO_E", BoschChecksum.Name(0x01));
        Assert.Equal("SB_ADD16_ALGO_E", BoschChecksum.Name(0x10));
    }
}
