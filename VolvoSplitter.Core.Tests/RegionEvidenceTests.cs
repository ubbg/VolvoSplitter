using System.Text;
using VolvoSplitter.Core;

namespace VolvoSplitter.Core.Tests;

/// <summary>
/// Die Beleglage der Bereichseinordnung: was „gesichert" tragen darf und was
/// nicht. Beide Fälle hier sind am Bestand von 1516 echten VAG-EDC17-Abbildern
/// gemessen — die Bytefolgen in den Tests stehen wörtlich so darin.
/// </summary>
public class RegionEvidenceTests
{
    private const int RegionSize = 0x8000;

    /// <summary>
    /// Rauschen mit voller Entropie. Deterministisch, damit ein Fehlschlag
    /// reproduzierbar ist — und ohne druckbare Läufe nennenswerter Länge.
    /// </summary>
    private static byte[] Noise(int length = RegionSize, uint seed = 0x5EED)
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

    /// <summary>Der einzige Bereich eines Abbilds ohne Sektoren.</summary>
    private static FlashRegion Only(byte[] data) =>
        Assert.Single(RegionScanner.Scan(data, data.Length, []));

    private static void Put(byte[] data, int at, string text) =>
        Encoding.ASCII.GetBytes(text).CopyTo(data, at);

    // ------------------------------------------------------------------
    // Quelldateipfade als Beleg (Befund 2.3)
    // ------------------------------------------------------------------

    [Fact]
    public void DotDotSlashInNoise_IsNotPlainCode()
    {
        // Der gemessene Fall: drei Bytes im Rauschen. Über 1516 Abbilder wurde
        // die Heuristik ausschließlich so ausgelöst — 80 Vorkommen von „../",
        // kein einziges von „src/" oder „@(#)" — und jeder der so gemeldeten
        // Bereiche war falsch. Entropie 8,00 und „Programmcode im Klartext"
        // schließen einander aus.
        var data = Noise();
        Put(data, 0x1000, "../");

        var region = Only(data);

        Assert.Equal(RegionKind.Opaque, region.Kind);
        Assert.Equal(RegionConfidence.Unknown, region.Confidence);
        Assert.DoesNotContain("Quelldateipfad", region.Description);
    }

    [Fact]
    public void LongestPrintableRunInTheCorpus_IsStillNotEvidence()
    {
        // „VV=VV../" ist der längste druckbare Lauf, der im ganzen Bestand eine
        // der drei Nadeln enthält: 8 Byte. Er liegt damit unter der Schwelle,
        // und das ist die Kalibrierung dieser Schwelle.
        var data = Noise();
        Put(data, 0x1000, "VV=VV../");

        Assert.NotEqual(RegionKind.Code, Only(data).Kind);
    }

    [Fact]
    public void DotDotSlashInARisingAxis_StaysData()
    {
        // Der zweite gemessene Kontext: eine steigende 16-Bit-Achse, in der
        // „../" schlicht als Zahlenwert vorkommt. Die Bytes stammen wörtlich
        // aus EDC17CP14, VW_Original_03L906022PB… bei 0x1000000.
        var data = new byte[RegionSize];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(0x40 + (i % 64) / 4);

        byte[] axis = [0x2C, 0xF0, 0x2C, 0x81, 0x2D, 0x11, 0x2E, 0xA0,
                       0x2E, 0x2E, 0x2F, 0xBB, 0x2F, 0x46, 0x30, 0xD0];
        axis.CopyTo(data, 0x1000);

        var region = Only(data);

        Assert.Equal(RegionKind.Data, region.Kind);
        Assert.NotEqual(RegionConfidence.Confirmed, region.Confidence);
    }

    [Fact]
    public void SourcePathInsideAString_CountsAsPlainCode()
    {
        // Die Gegenprobe: derselbe Dreibytefund, aber in einer Zeichenkette.
        // Das ist der Fall, für den die Heuristik gebaut ist — er muss bleiben,
        // sonst wäre die Korrektur eine Überkorrektur.
        var data = new byte[RegionSize];
        const string Assert_ = "../src/appl/ecu_main.c: assert failed\n";
        for (int at = 0; at + Assert_.Length < data.Length; at += Assert_.Length)
            Put(data, at, Assert_);

        var region = Only(data);

        Assert.Equal(RegionKind.Code, region.Kind);
        Assert.Equal(RegionConfidence.Confirmed, region.Confidence);
        Assert.Contains("../src/appl/ecu_main.c", region.Description);
    }

    [Fact]
    public void SourcePathInAMaximumEntropyRegion_StaysOpaque()
    {
        // Auch ein echter Pfad hebelt die Opak-Erkennung nicht mehr aus: bei
        // Entropie 8,00 ist eine Zeichenkette ein Bruchteil des Bereichs, und
        // „opak" bleibt die ehrlichere Aussage über das Ganze.
        var data = Noise();
        Put(data, 0x1000, "../src/appl/ecu_main.c: assert failed");

        Assert.Equal(RegionKind.Opaque, Only(data).Kind);
    }

    // ------------------------------------------------------------------
    // Konstantes Füllbyte (Befund 2.4)
    // ------------------------------------------------------------------

    [Fact]
    public void TrulyConstantRegion_IsConfirmed()
    {
        var region = Only(new byte[RegionSize]);

        Assert.Equal("Konstantes Füllbyte 0x00", region.Description);
        Assert.Equal(RegionConfidence.Confirmed, region.Confidence);
    }

    [Fact]
    public void NearlyConstantRegion_NamesTheDominantByte_AndIsNotConfirmed()
    {
        // Nachgebaut aus EDC17U05: 8 192 B, davon 8 187 × 0x00 — gemeldet wurde
        // „Konstantes Füllbyte 0x1D / gesichert", weil 0x1D zufällig das erste
        // Byte war und im ganzen Bereich genau einmal vorkam.
        var data = new byte[0x2000];
        data[0] = 0x1D;
        data[0x100] = 0x3C;
        data[0x200] = 0x7A;
        data[0x300] = 0xB1;
        data[0x400] = 0xE6;

        var region = Only(data);

        Assert.DoesNotContain("Konstantes", region.Description);
        Assert.DoesNotContain("0x1D", region.Description);
        Assert.Contains("0x00", region.Description);
        Assert.Contains("5 abweichende Bytes", region.Description);
        Assert.Equal(RegionConfidence.Strong, region.Confidence);
    }

    [Fact]
    public void DominantByte_ReportsConstancyExactly()
    {
        var flat = new byte[64];
        Array.Fill(flat, (byte)0xC3);
        Assert.Equal(((byte)0xC3, 64L), BinaryHeuristics.DominantByte(flat, 0, 64));

        // Ein einziges abweichendes Byte am Anfang: das häufigste bleibt 0xC3,
        // die Anzahl aber kleiner als die Länge — genau daran hängt „gesichert".
        flat[0] = 0x80;
        Assert.Equal(((byte)0xC3, 63L), BinaryHeuristics.DominantByte(flat, 0, 64));
    }

    [Fact]
    public void PrintableRun_MeasuresTheStringAroundAHit()
    {
        ReadOnlySpan<byte> window = "\x01\x02VV=VV../\x03\x04"u8;

        var (from, to) = BinaryHeuristics.PrintableRun(window, 7);   // das „/"
        Assert.Equal(2, from);
        Assert.Equal(10, to);

        // Auf einem nicht druckbaren Byte gibt es keinen Lauf.
        Assert.Equal((0, 0), BinaryHeuristics.PrintableRun(window, 0));
    }
}
