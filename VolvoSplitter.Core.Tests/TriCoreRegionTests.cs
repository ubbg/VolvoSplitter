using System.Text;
using VolvoSplitter.Core;
using VolvoSplitter.Core.TriCore;

namespace VolvoSplitter.Core.Tests;

public class TriCoreRegionTests
{
    private const long TwoMib = 0x200000;

    /// <summary>
    /// 2-MiB-TC1797-Abbild ohne Blockkette: ein Codeblock für die Zeigerdichte,
    /// ein Kennfeldblock auf einer Löschsektorgrenze und die Typkennung, damit
    /// der Baustein — und damit die Sektorkarte — feststeht.
    /// </summary>
    private static FlashDump WithoutChain(params (long At, byte[] Bytes)[] extra)
    {
        var parts = new List<(long, byte[])>
        {
            (0x100000, TriCoreDump.CodeBlock(0x40000, 0x80000000)),
            (0x1F0000, Encoding.ASCII.GetBytes("Steuergeraet EDC17C54"))
        };
        parts.AddRange(extra.Select(e => (e.At, e.Bytes)));

        return FlashDump.FromBytes(TriCoreDump.Pflash(TwoMib, [.. parts]), "tricore.bin");
    }

    [Fact]
    public void CalibrationCandidate_IsNotCalledCalibration()
    {
        // Entropie im Datenband, Mindestgröße, monotone Fenster und Beginn auf
        // einer Löschsektorgrenze — mehr sagt das Abbild nicht her. Also heißt
        // es „Kandidat", nicht „Kalibrierungssektor".
        var dump = WithoutChain((0x040000, TriCoreDump.CalibrationBlock(0x40000)));

        Assert.Equal("TC1797", dump.Profile.MicroName);

        var region = Assert.Single(dump.Regions, r => r.Start == 0x040000);

        Assert.Equal(RegionKind.Data, region.Kind);
        Assert.NotEqual(RegionConfidence.Confirmed, region.Confidence);
        Assert.Contains("Kalibrierungskandidat", region.Label);
        Assert.DoesNotContain("Kalibrierungssektor", region.Label);
        Assert.DoesNotContain("Kalibrierungssektor", region.Description);
        Assert.Contains("nicht als Kalibrierung belegt", region.Description);
    }

    [Fact]
    public void CalibrationLikeDataOffSectorBoundary_StaysPlainData()
    {
        // Derselbe Inhalt, aber nicht auf einer Löschsektorgrenze: dann fehlt
        // ein Merkmal, und es bleibt schlicht „Daten".
        var dump = WithoutChain((0x044000, TriCoreDump.CalibrationBlock(0x40000)));

        var region = Assert.Single(dump.Regions, r => r.Start == 0x044000);

        Assert.Equal(RegionKind.Data, region.Kind);
        Assert.Equal("Daten", region.Label);
        Assert.DoesNotContain("Kalibrierungskandidat", region.Label);
    }

    [Fact]
    public void ShortCalibrationLikeRegion_StaysPlainData()
    {
        // Auf der Grenze, aber unter 128 KiB.
        var dump = WithoutChain((0x040000, TriCoreDump.CalibrationBlock(0x10000)));

        var region = Assert.Single(dump.Regions, r => r.Start == 0x040000);
        Assert.Equal("Daten", region.Label);
    }

    [Fact]
    public void NoCalibrationCandidateWithoutAKnownSectorMap()
    {
        // Ohne bestimmten Baustein gibt es keine Löschsektorgrenzen — und damit
        // auch keinen Kandidaten. Lieber eine Aussage weniger.
        var dump = FlashDump.FromBytes(TriCoreDump.Pflash(TwoMib,
            (0x100000, TriCoreDump.CodeBlock(0x40000, 0x80000000)),
            (0x040000, TriCoreDump.CalibrationBlock(0x40000))), "unbestimmt.bin");

        Assert.True(dump.Detection.DeviceAmbiguous);
        Assert.Empty(dump.Layout!.EraseSectors);

        var region = Assert.Single(dump.Regions, r => r.Start == 0x040000);
        Assert.Equal("Daten", region.Label);
    }

    [Fact]
    public void VolvoEcuParser_DoesNotRunOnTriCoreImages()
    {
        // Ein TriCore-Abbild wird gar nicht erst gegen ein Volvo-Containerformat
        // geprüft — auch dann nicht, wenn die Bytes zufällig passen.
        var volvo = TestDump.VolvoEcu(0x010000, "23310625P01", 0x4000);
        var dump = WithoutChain((0x040000, volvo));

        Assert.Equal(ContainerKind.BoschBlockChain, dump.Profile.Container);
        Assert.DoesNotContain(dump.Regions, r => r.Label.Contains("VOLVOECU"));
    }

    [Fact]
    public void SccsMarker_CountsAsPlainCode()
    {
        // „@(#)" ist die SCCS-Kennung, die in Bosch-Ständen üblich ist; sie
        // belegt unverschlüsselten Code so gut wie ein Quelldateipfad.
        var text = Encoding.ASCII.GetBytes("@(#) ecu_main.c 1.42 released\n");
        var payload = new byte[0x2000];
        for (int i = 0; i + text.Length < payload.Length; i += text.Length)
            text.CopyTo(payload, i);

        var dump = WithoutChain((0x040000, payload));

        var region = Assert.Single(dump.Regions, r => r.Start == 0x040000);
        Assert.Equal(RegionKind.Code, region.Kind);
        Assert.Equal(RegionConfidence.Confirmed, region.Confidence);
    }

    [Fact]
    public void MonotonicRunRatio_SeparatesRampsFromNoise()
    {
        var ramp = TriCoreDump.CalibrationBlock(0x4000);
        var noise = TriCoreDump.CodeBlock(0x4000, 0x80000000, pointerEvery: 4096);

        Assert.True(BinaryHeuristics.MonotonicRunRatio(ramp, 0, ramp.Length) > 0.9);
        Assert.True(BinaryHeuristics.MonotonicRunRatio(noise, 0, noise.Length) < 0.05);

        // Eine konstante Folge ist trivial monoton und zählt deshalb nicht.
        var flat = new byte[0x4000];
        Assert.Equal(0, BinaryHeuristics.MonotonicRunRatio(flat, 0, flat.Length));
    }
}
