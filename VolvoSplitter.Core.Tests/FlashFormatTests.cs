using VolvoSplitter.Core;

namespace VolvoSplitter.Core.Tests;

public class FlashFormatTests
{
    [Fact]
    public void SectorLength_FollowsFormatFormula()
    {
        // Länge = Endadresse - CPU-Offset + 44
        Assert.Equal(0x8000 + 44, FlashFormat.SectorLength(0x108000, 0x100000));
    }

    [Theory]
    [InlineData(0x400000, EcuFamily.Ems23)]
    [InlineData(0x500000, EcuFamily.Ems23)]
    [InlineData(0x500001, EcuFamily.Ems24)]
    [InlineData(0x800000, EcuFamily.Ems24)]
    public void DetectFamily_SplitsAtThreshold(long size, EcuFamily expected)
    {
        Assert.Equal(expected, FlashFormat.DetectFamily(size));
    }

    [Fact]
    public void FlashSize_DependsOnFamily()
    {
        Assert.Equal(0x400000, FlashFormat.FlashSizeFor(EcuFamily.Ems23));
        Assert.Equal(0x800000, FlashFormat.FlashSizeFor(EcuFamily.Ems24));
    }
}
