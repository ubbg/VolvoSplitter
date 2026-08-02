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

    [Fact]
    public void HighEntropy_IsClassifiedAsOpaque()
    {
        var data = Blank(0x8000);
        uint s = 0x1234567;
        for (int i = 0x1000; i < 0x3000; i++)
        {
            s = s * 1664525 + 1013904223;   // LCG — gleichverteilte Bytes
            data[i] = (byte)(s >> 24);
        }

        var region = Assert.Single(RegionScanner.Scan(data, 0x8000, NoSectors));
        Assert.Equal(RegionKind.Opaque, region.Kind);
        Assert.True(region.Entropy >= 7.9);
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
    public void BeyondFlashSize_IsClassifiedAsEeprom()
    {
        var data = Blank(0x5000);
        for (int i = 0x3000; i < 0x4000; i++) data[i] = 0x11;

        // Flash endet bei 0x2000 — alles dahinter ist der angehängte EEPROM-Auszug.
        var region = Assert.Single(RegionScanner.Scan(data, 0x2000, NoSectors));
        Assert.Equal(RegionKind.Eeprom, region.Kind);
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
