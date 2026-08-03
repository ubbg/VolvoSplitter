using System.Text;
using VolvoSplitter.Core;

namespace VolvoSplitter.Core.Tests;

public class RegionScannerTests
{
    private static readonly SectorInfo[] NoSectors = [];

    private static byte[] Blank(int size)
    {
        var data = new byte[size];
        Array.Fill(data, (byte)0xFF);
        return data;
    }

    [Fact]
    public void ErasedImage_YieldsNoRegions()
    {
        var regions = RegionScanner.Scan(Blank(0x8000), 0x8000, NoSectors);
        Assert.Empty(regions);
    }

    [Fact]
    public void ConstantFill_IsClassifiedAsData()
    {
        var data = Blank(0x8000);
        for (int i = 0x1000; i < 0x2000; i++) data[i] = 0x42;

        var region = Assert.Single(RegionScanner.Scan(data, 0x8000, NoSectors));
        Assert.Equal(RegionKind.Data, region.Kind);
        Assert.Contains("42", region.Description);
    }

    private static byte[] HighEntropyImage()
    {
        var data = Blank(0x8000);
        uint s = 0x1234567;
        for (int i = 0x1000; i < 0x3000; i++)
        {
            s = s * 1664525 + 1013904223;   // LCG — gleichverteilte Bytes
            data[i] = (byte)(s >> 24);
        }
        return data;
    }

    [Fact]
    public void HighEntropy_IsClassifiedAsOpaque()
    {
        var region = Assert.Single(RegionScanner.Scan(HighEntropyImage(), 0x8000, NoSectors));
        Assert.Equal(RegionKind.Opaque, region.Kind);
        Assert.True(region.Entropy >= 7.9);
    }

    [Fact]
    public void OpaqueRegion_DoesNotClaimEncryptionAsFact()
    {
        var region = Assert.Single(RegionScanner.Scan(HighEntropyImage(), 0x8000, NoSectors));

        // Hohe Entropie ist eine Beobachtung. Chiffretext, komprimierte Daten und
        // signierte Container sehen gleich aus — die Anzeige darf sich nicht festlegen.
        Assert.Equal("Opaker Block", region.Label);
        Assert.Contains("möglich", region.Description);
        Assert.Contains("nicht belegt", region.Description);
        Assert.Equal(RegionConfidence.Unknown, region.Confidence);
    }

    [Fact]
    public void SourcePaths_AreClassifiedAsCode()
    {
        var data = Blank(0x8000);
        byte[] text = Encoding.ASCII.GetBytes("assert failed at ../src/engine/fuel.c line 42\n");
        for (int i = 0x1000; i + text.Length < 0x2000; i += text.Length)
            text.CopyTo(data, i);

        var region = Assert.Single(RegionScanner.Scan(data, 0x8000, NoSectors));
        Assert.Equal(RegionKind.Code, region.Kind);
    }

    [Fact]
    public void RegionBeyondLargeFlash_IsNotAutomaticallyEeprom()
    {
        var data = Blank(0x5000);
        for (int i = 0x3000; i < 0x4000; i++) data[i] = 0x11;

        // Früher galt alles hinter dem Flash-Baustein pauschal als EEPROM, ohne
        // den Inhalt anzusehen. Jetzt entscheidet der Inhalt: konstante Füllung.
        var region = Assert.Single(RegionScanner.Scan(data, 0x2000, NoSectors));

        Assert.Equal(RegionKind.Data, region.Kind);
        Assert.NotEqual(RegionKind.NvmData, region.Kind);
        Assert.Contains("11", region.Description);

        // Ohne Layout bleibt die Herkunft offen — und wird auch so benannt.
        Assert.Contains("Herkunft nicht bestimmt", region.Description);
        Assert.Equal(RegionConfidence.Unknown, region.Confidence);
        Assert.Null(region.CpuAddressRange);
    }

    [Fact]
    public void VolvoEcuBlock_IsClassifiedAsCode()
    {
        var image = TestDump.Image(Mpc5777cLayout.ContainerSize,
                                   (0x810000, TestDump.VolvoEcu(0x010000, "23310625P01", 0x4000)));
        var dump = FlashDump.FromBytes(image);

        var region = Assert.Single(dump.Regions);

        // Ein ausführbares Modul im Low-Flash — kein EEPROM-Datensatz.
        Assert.Equal(RegionKind.Code, region.Kind);
        Assert.Contains("VOLVOECU", region.Label);
        Assert.Contains("23310625P01", region.Description);
        Assert.Equal(RegionConfidence.Confirmed, region.Confidence);

        // Datei-Offset und CPU-Adresse werden getrennt geführt.
        Assert.Equal(0x810000L, region.Start);
        Assert.Equal(0x010000L, region.CpuStart);
        Assert.Equal("Low-Block 1", region.PartitionLabel);
    }

    [Fact]
    public void NvmBlock_ReportsKnownSectorChecksumReferences()
    {
        // Kalibrierungssektor an seiner Standardadresse, damit sein Prüfwert
        // eine bekannte Referenz ist.
        var cal = TestDump.Sector(0xF40000, "23310625P01", 0x1000);
        uint calCrc = TestDump.ReadBe(cal, cal.Length - FlashFormat.CrcTrailerLen);

        // NVM-Block wie im echten Dump: Kopfwörter, Prüfsummenkopie, Laufzeit-Records.
        var nvm = new byte[0x2000];
        Array.Fill(nvm, (byte)0xFF);
        TestDump.WriteBe(nvm, 0x00, 0x00000F53);
        TestDump.WriteBe(nvm, 0x04, 0xF1C259BC);
        TestDump.WriteBe(nvm, 0x0C, calCrc);
        Encoding.ASCII.GetBytes("UPTIME 20210414").CopyTo(nvm, 0x1000);

        var dump = FlashDump.FromBytes(TestDump.Image(Mpc5777cLayout.ContainerSize,
                                                     (0x740000, cal), (0x820000, nvm)));

        var region = Assert.Single(dump.Regions, r => r.PartitionLabel == "Mid-Block 0");

        Assert.Equal(RegionKind.NvmData, region.Kind);
        Assert.Contains("UPTIME", region.Description);
        Assert.Contains("Kalibrierung", region.Description);
        Assert.Contains($"0x{calCrc:X8}", region.Description);
        Assert.Equal(0x020000L, region.CpuStart);
    }

    [Fact]
    public void NvmBlockWithGapBetweenHeaderAndRecords_IsStillReported()
    {
        // Kopf am Blockanfang, Records weit dahinter, dazwischen gelöschtes
        // Flash. Einzeln betrachtet wäre jedes Bruchstück zu klein zum Melden —
        // der Block muss trotzdem als Ganzes erscheinen.
        var nvm = new byte[0xC100];
        Array.Fill(nvm, (byte)0xFF);
        TestDump.WriteBe(nvm, 0x00, 0x00000F53);
        Encoding.ASCII.GetBytes("UPTIME 20260731").CopyTo(nvm, 0xC000);

        var dump = FlashDump.FromBytes(TestDump.Image(Mpc5777cLayout.ContainerSize, (0x820000, nvm)));

        var region = Assert.Single(dump.Regions);
        Assert.Equal(RegionKind.NvmData, region.Kind);
        Assert.Equal(0x820000L, region.Start);
        Assert.Equal(0x82C00FL, region.End);   // bis zum letzten belegten Byte
    }

    [Fact]
    public void CodeInLowFlash_IsNotReportedAsNvmData()
    {
        // Derselbe physische Blocktyp, aber ohne jedes NVM-Anzeichen: die
        // Partition allein darf die Einordnung nicht bestimmen.
        var text = Encoding.ASCII.GetBytes("assert failed at ../src/engine/fuel.c line 42\n");
        var payload = new byte[0x2000];
        for (int i = 0; i + text.Length < payload.Length; i += text.Length)
            text.CopyTo(payload, i);

        var dump = FlashDump.FromBytes(TestDump.Image(Mpc5777cLayout.ContainerSize,
                                                     (0x800000, payload)));

        var region = Assert.Single(dump.Regions);
        Assert.Equal(RegionKind.Code, region.Kind);
    }

    [Fact]
    public void ClaimedSectorRange_IsExcluded()
    {
        var data = Blank(0x8000);
        for (int i = 0x1000; i < 0x2000; i++) data[i] = 0x42;

        var sector = new SectorInfo
        {
            Kind = SectorKind.Asw, Label = "ASW", Prefix = "asw_",
            Start = 0x1000, CpuOffset = 0x1000, Status = SectorStatus.Verified,
            Length = 0x1000
        };

        var regions = RegionScanner.Scan(data, 0x8000, [sector]);
        Assert.Empty(regions);   // der belegte Bereich gehört dem Sektor
    }
}
