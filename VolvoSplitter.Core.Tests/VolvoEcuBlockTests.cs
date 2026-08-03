using VolvoSplitter.Core;

namespace VolvoSplitter.Core.Tests;

public class VolvoEcuBlockTests
{
    private const long LowBlock1 = 0x010000;   // CPU-Anfang des zweiten Low-Blocks

    [Fact]
    public void VolvoEcuHeader_MapsStartAndEndAddresses()
    {
        var block = TestDump.VolvoEcu(LowBlock1, "23310625P01", 0xE27C);

        Assert.True(VolvoEcuBlock.TryParse(block, 0, out var parsed));
        Assert.NotNull(parsed);

        // Kopf nennt CPU-Adressen; die Basis ergibt sich aus Codebeginn - 0x400.
        Assert.Equal(LowBlock1 + 0x400, parsed!.CodeStart);
        Assert.Equal(LowBlock1 + 0xE278, parsed.CrcAddress);
        Assert.Equal(LowBlock1, parsed.CpuStart);
        Assert.Equal(0xE27C, parsed.Length);
    }

    [Fact]
    public void VolvoEcuBlock_ValidatesWholeBlockCrc32()
    {
        var block = TestDump.VolvoEcu(LowBlock1, "23310625P01", 0x4000);

        Assert.True(VolvoEcuBlock.TryParse(block, 0, out var parsed));
        Assert.True(parsed!.CrcOk);
        Assert.Equal(parsed.CrcStored, parsed.CrcComputed);

        // Ein gekipptes Byte im Kopfbereich muss auffallen — die CRC läuft
        // anders als beim Sektorformat über den ganzen Block, nicht erst ab 0x0F8.
        block[0x20] ^= 0xFF;
        Assert.True(VolvoEcuBlock.TryParse(block, 0, out var broken));
        Assert.False(broken!.CrcOk);
    }

    [Fact]
    public void VolvoEcuBlock_ReadsPartNumber()
    {
        var block = TestDump.VolvoEcu(LowBlock1, "23504791P02", 0x4000);

        Assert.True(VolvoEcuBlock.TryParse(block, 0, out var parsed));
        Assert.Equal("23504791P02", parsed!.PartNumber);
    }

    [Fact]
    public void VolvoEcuBlock_FoundAtFileOffset_KeepsFileAndCpuApart()
    {
        // Der Block liegt bei Datei-Offset 0x810000, im CPU-Raum aber bei 0x010000.
        var image = TestDump.Image(Mpc5777cLayout.ContainerSize,
                                   (0x810000, TestDump.VolvoEcu(LowBlock1, "23310625P01", 0x4000)));

        Assert.True(VolvoEcuBlock.TryParse(image, 0x810000, out var parsed));
        Assert.Equal(0x810000, parsed!.FileStart);
        Assert.Equal(LowBlock1, parsed.CpuStart);
        Assert.True(parsed.CrcOk);

        // Gegenprobe: die aus dem Block selbst hergeleitete CPU-Basis stimmt mit
        // dem Layout überein — eine unabhängige Bestätigung der Containerabbildung.
        var layout = Mpc5777cLayout.For(EcuFamily.Ems24, Mpc5777cLayout.ContainerSize);
        Assert.Equal(parsed.CpuStart, layout!.PartitionAt(0x810000)!.CpuStart);
    }

    [Fact]
    public void WithoutMagic_NothingIsParsed()
    {
        var block = TestDump.VolvoEcu(LowBlock1, "23310625P01", 0x4000);
        block[0] = (byte)'X';

        Assert.False(VolvoEcuBlock.TryParse(block, 0, out var parsed));
        Assert.Null(parsed);
    }
}
