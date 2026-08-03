using System.Text;
using VolvoSplitter.Core;
using VolvoSplitter.Core.TriCore;

namespace VolvoSplitter.Core.Tests;

public class EcuDetectorTests
{
    private const long TwoMib = 0x200000;
    private const long FourMib = 0x400000;

    /// <summary>Codeähnlicher Block, dessen Zeigerdichte über der Schwelle liegt.</summary>
    private static byte[] PointerRichImage(long size, params (long At, byte[] Bytes)[] extra)
    {
        var parts = new List<(long, byte[])> { (0x100000, TriCoreDump.CodeBlock(0x40000, 0x80000000)) };
        parts.AddRange(extra.Select(e => (e.At, e.Bytes)));
        return TriCoreDump.Pflash(size, [.. parts]);
    }

    private static byte[] Text(string value) => Encoding.ASCII.GetBytes(value);

    // ==================================================================
    // TriCore
    // ==================================================================

    [Fact]
    public void SampleMed17Image_IsIdentifiedAsTriCoreWithChain()
    {
        var dump = FlashDump.FromBytes(TriCoreDump.SampleMed17Image(), "med17.bin");

        Assert.Equal(ContainerKind.BoschBlockChain, dump.Profile.Container);
        Assert.Equal("TC1797", dump.Profile.MicroName);
        Assert.Equal(Endianness.Little, dump.Profile.Endianness);
        Assert.Equal(8, dump.Sectors.Count);
        Assert.All(dump.Sectors, s => Assert.Equal(SectorStatus.Verified, s.Status));

        Assert.Contains(dump.Detection.Evidence, e => e.Contains("rechnerisch bestätigter Prüfsumme"));
        Assert.False(dump.Detection.Ambiguous);
    }

    [Fact]
    public void ExtractedBlockNames_AreUniquePerBlock()
    {
        // Vier Blöcke teilen sich die Kennung 10SW008917 — die Dateinamen
        // dürfen sich trotzdem nicht überschreiben.
        var dump = FlashDump.FromBytes(TriCoreDump.SampleMed17Image(), "med17.bin");

        Assert.Equal(dump.Sectors.Count, dump.Sectors.Select(s => s.OutputName).Distinct().Count());
    }

    [Fact]
    public void TriCoreImage_WithoutChain_ClaimsNoVerifiedBlocks()
    {
        // Plausibler Code und Kennfelder, aber keine Blockkette: dann gibt es
        // keine bestätigten Blöcke, nur Bereiche.
        var image = PointerRichImage(TwoMib, (0x040000, TriCoreDump.CalibrationBlock(0x40000)));
        var dump = FlashDump.FromBytes(image, "ohne_kette.bin");

        Assert.Equal(ContainerKind.BoschBlockChain, dump.Profile.Container);
        Assert.Empty(dump.Chain.Blocks);
        Assert.DoesNotContain(dump.Sectors, s => s.Status == SectorStatus.Verified);
        Assert.NotEmpty(dump.Regions);
    }

    [Fact]
    public void TwoMegabyteImage_WithoutEcuString_DoesNotPickASingleMcu()
    {
        // TC1796 und TC1797-PMU0 bilden dieselben 2 MiB gleich ab. Ohne
        // Kennung lässt sich nicht entscheiden — also wird nicht entschieden.
        var image = TriCoreDump.Pflash(TwoMib,
            (0x018000, TriCoreDump.Block(0x10, 0x80018000, 0x2000, 0, "10SW008917")));

        var dump = FlashDump.FromBytes(image, "zweideutig.bin");

        Assert.True(dump.Detection.DeviceAmbiguous);
        Assert.Equal(["TC1796", "TC1797"], dump.Detection.DeviceCandidates);
        Assert.DoesNotContain(dump.Profile.MicroName, new[] { "TC1796", "TC1797" });
        Assert.Empty(dump.Layout!.EraseSectors);
        Assert.Contains(dump.Detection.Evidence, e => e.Contains("Baustein nicht bestimmt"));
    }

    [Fact]
    public void EcuTypeString_SelectsTheMatchingMcu()
    {
        // EDC17CP44 ist laut Tabelle eindeutig ein TC1797.
        var image = PointerRichImage(TwoMib, (0x1F0000, Text("VAG EDC17CP44 0281020088")));
        var dump = FlashDump.FromBytes(image, "edc17cp44.bin");

        Assert.Equal("TC1797", dump.Profile.MicroName);
        Assert.False(dump.Detection.DeviceAmbiguous);
        Assert.NotEmpty(dump.Layout!.EraseSectors);
    }

    [Fact]
    public void ContinuousProgramFlash_OutweighsTheTabulatedTwoBankSplit()
    {
        // EDC17CP44-Abbilder (Bosch 1037540589) legen Blöcke auf 0x80200000 und
        // 0x80340000 — Adressen, die es bei zwei 2-MiB-Bänken gar nicht gibt.
        // Die Tabelle nennt TC1797, das Abbild widerspricht. Ohne diese Messung
        // fielen die beiden oberen Blöcke durch die blockEnd-Regel und fehlten
        // stillschweigend: ein nicht gefundener Block sieht aus wie keiner.
        var image = TriCoreDump.Pflash(FourMib,
            (0x000000, TriCoreDump.Block(0x10, 0x80000000, 0x4000, 0x80200000, "1037540589")),
            (0x1F0000, Text("VAG EDC17CP44 0281020088")),
            (0x200000, TriCoreDump.Block(0x80, 0x80200000, 0x4000, 0x80340000, "1037540589")),
            (0x340000, TriCoreDump.Block(0x60, 0x80340000, 0x4000, 0, "1037540589")));

        var dump = FlashDump.FromBytes(image, "durchgehend.bin");

        Assert.Equal(3, dump.Sectors.Count);
        Assert.All(dump.Sectors, s => Assert.Equal(SectorStatus.Verified, s.Status));
        Assert.Contains(dump.Detection.Evidence,
                        e => e.Contains("durchgehendem PFLASH") && e.Contains("TC1797"));
    }

    [Fact]
    public void AmbiguousEcuType_LeavesTheMcuOpen()
    {
        // EDC17CP20 lässt TC1796 und TC1797 zu — dann wird nichts gewählt.
        var image = PointerRichImage(TwoMib, (0x1F0000, Text("VAG EDC17CP20 0281020088")));
        var dump = FlashDump.FromBytes(image, "edc17cp20.bin");

        Assert.True(dump.Detection.DeviceAmbiguous);
        Assert.Contains(dump.Detection.Evidence, e => e.Contains("EDC17CP20"));
    }

    [Fact]
    public void ImageContentOutranksTheEcuTable()
    {
        // Die Steuergerätetabelle ist eine Nutzerangabe, die Bankgrenze im
        // Abbild eine Messung. Behauptet die Kennung einen Baustein, dessen
        // Aufteilung weniger Blockköpfe bestätigt, folgt das Werkzeug dem Abbild.
        var image = TriCoreDump.SampleMed17Image();

        // EDC17C46 steht laut Tabelle für einen TC1767 — dessen Aufteilung
        // kann die Blöcke in PMU1 und im externen Flash nicht erklären. Die
        // Zeichenkette liegt in einer Lücke vor dem Dataset-Block und wird
        // deshalb vom freien Suchlauf zuerst gefunden.
        Text("Steuergeraet EDC17C46 fuer den Test").CopyTo(image, 0x012000);

        var dump = FlashDump.FromBytes(image, "widerspruch.bin");

        Assert.Equal("TC1797", dump.Profile.MicroName);
        Assert.Contains(dump.Detection.Evidence, e => e.Contains("Der Abbildinhalt widerspricht"));
        Assert.Equal(8, dump.Chain.Blocks.Count);
    }

    [Fact]
    public void EcuReportMicro_OutranksEverythingElse()
    {
        string path = TestDump.TempPath(".bin");
        string report = Path.ChangeExtension(path, ".TXT");

        try
        {
            File.WriteAllBytes(path, PointerRichImage(TwoMib));
            File.WriteAllText(report, "Plugin: Bench\nMicro: TC1796\n");

            var dump = FlashDump.Load(path);

            Assert.Equal("TC1796", dump.Profile.MicroName);
            Assert.Contains(dump.Detection.Evidence, e => e.Contains("Protokolldatei nennt Micro"));
        }
        finally
        {
            File.Delete(path);
            File.Delete(report);
        }
    }

    [Fact]
    public void DflashOnlyFile_IsNotMistakenForPflash()
    {
        // Auslesewerkzeuge legen den Datenflash als eigene Datei ab. Allein
        // geladen ist das kein geschrumpftes PFLASH, sondern schlicht unbekannt.
        var data = new byte[0x20000];
        uint s = 0x5EED;
        for (int i = 0; i < data.Length; i++) { s = s * 1664525 + 1013904223; data[i] = (byte)(s >> 24); }

        var dump = FlashDump.FromBytes(data, "ecu_Eeprom.bin");

        Assert.Equal("unknown", dump.Profile.Key);
        Assert.Equal(ContainerKind.None, dump.Profile.Container);
        Assert.Null(dump.Layout);
        Assert.Empty(dump.Sectors);
        Assert.Contains(dump.Detection.Evidence, e => e.Contains("Mindestpunktzahl"));
    }

    [Fact]
    public void TriCoreProfile_DoesNotOfferWriteBack()
    {
        var dump = FlashDump.FromBytes(TriCoreDump.SampleMed17Image(), "med17.bin");
        var block = dump.Sectors[0];

        Assert.False(dump.Profile.SupportsWriteBack);

        Assert.Throws<InvalidOperationException>(() => dump.RepairCrc(block));
        Assert.Throws<InvalidOperationException>(() => dump.ReplaceSector(block, new byte[0x1000], true));
        Assert.Throws<InvalidOperationException>(() => dump.PatchUInt32Be(0, 0));
        Assert.Throws<InvalidOperationException>(() => dump.Save(TestDump.TempPath()));

        Assert.False(dump.IsModified);
    }

    // ==================================================================
    // TRW — die Erkennung darf sich nicht verschlechtern
    // ==================================================================

    [Fact]
    public void EmsImage_StillDetectedAfterTriCoreSupport()
    {
        var ems24 = TestDump.Image(Mpc5777cLayout.ContainerSize,
            (0x740000, TestDump.Sector(0xF40000, "31399478 AA", 0x2000)));
        var ems23 = TestDump.Image(0x400000,
            (0x060000, TestDump.Sector(0x060000, "31399478 AA", 0x2000)));

        var dump24 = FlashDump.FromBytes(ems24, "ems24.mpc");
        var dump23 = FlashDump.FromBytes(ems23, "ems23.mpc");

        Assert.Equal(EcuFamily.Ems24, dump24.Profile.Family);
        Assert.Equal("MPC5777C", dump24.Profile.MicroName);
        Assert.True(dump24.Profile.SupportsWriteBack);

        Assert.Equal(EcuFamily.Ems23, dump23.Profile.Family);
        Assert.Equal("MPC5674F", dump23.Profile.MicroName);
    }

    [Fact]
    public void Detection_IsAmbiguous_WhenBothFamiliesScoreClose()
    {
        // Ein Sektorkopf, der zu keiner der beiden Tabellen passt: das genügt
        // für „TRW", aber nicht für die Familie. Dann wird das gesagt.
        var image = TestDump.Image(0x400000,
            (0x123000, TestDump.Sector(0x999000, "31399478 AA", 0x2000)));

        var detection = EcuDetector.Identify(image);

        Assert.True(detection.Ambiguous);
        Assert.NotNull(detection.RunnerName);
        Assert.True(detection.Score - detection.Runner < EcuDetector.AmbiguityMargin);
        Assert.Contains(detection.Evidence, e => e.Contains("nicht eindeutig"));
    }

    [Fact]
    public void FileSizeAloneNeverDecidesBetweenManufacturers()
    {
        // Eine 8-MiB-Datei ohne jeden Beleg darf weder EMS2.4 noch TriCore
        // heißen — die Größe passt zu beidem.
        var blank = new byte[0x800000];
        Array.Fill(blank, (byte)0xFF);

        var detection = EcuDetector.Identify(blank);

        Assert.Equal("unknown", detection.Profile.Key);
        Assert.True(detection.Score < EcuDetector.MinimumScore);
    }

    [Fact]
    public void ForcedProfile_SkipsDetection()
    {
        var image = TriCoreDump.SampleMed17Image();
        var dump = FlashDump.FromBytes(image, "med17.bin", forceProfile: "tc1797");

        Assert.Equal("TC1797", dump.Profile.MicroName);
        Assert.Equal(8, dump.Sectors.Count);
        Assert.Contains(dump.Detection.Evidence, e => e.Contains("von Hand vorgegeben"));

        Assert.Throws<ArgumentException>(
            () => FlashDump.FromBytes(image, "med17.bin", forceProfile: "gibtsnicht"));
    }
}
