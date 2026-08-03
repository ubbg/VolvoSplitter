using VolvoSplitter.Core;

namespace VolvoSplitter.Core.Tests;

public class Mpc5777cLayoutTests
{
    private static Mpc5777cLayout Full() =>
        Mpc5777cLayout.For(EcuFamily.Ems24, Mpc5777cLayout.ContainerSize)!;

    [Fact]
    public void ContainerSize_AccountsForEveryByte()
    {
        // 8 MiB Large Flash + 4 × 64 KiB Low/Mid + 16 KiB UTEST — ohne Rest.
        Assert.Equal(0x844000, Mpc5777cLayout.ContainerSize);
        Assert.Equal(Mpc5777cLayout.ContainerSize,
                     Mpc5777cLayout.LargeFlashSize + Mpc5777cLayout.LowMidSize + Mpc5777cLayout.UtestSize);
    }

    [Fact]
    public void Mpc5777cLayout_MapsLargeFlashAndLowMidPartitions()
    {
        var layout = Full();

        Assert.True(layout.Complete);
        Assert.Equal(6, layout.Partitions.Count);

        // Large Flash: Datei ab 0x000000, CPU ab 0x800000. Das bestätigt ein
        // Sektorkopf im echten Dump: bei 0x740000 steht der CPU-Offset 0xF40000.
        Assert.Equal(0xF40000L, layout.ToCpu(0x740000));

        // Low/Mid-Blöcke: Datei 0x800000..0x83FFFF, CPU 0x000000..0x03FFFF.
        Assert.Equal(0x000000L, layout.ToCpu(0x800000));
        Assert.Equal(0x010000L, layout.ToCpu(0x810000));
        Assert.Equal(0x020000L, layout.ToCpu(0x820000));
        Assert.Equal(0x030000L, layout.ToCpu(0x830000));

        // UTEST: Datei 0x840000, CPU 0x400000.
        Assert.Equal(0x400000L, layout.ToCpu(0x840000));
    }

    [Fact]
    public void LowMidPartitions_CarryBlockTypeAndRwwNumber()
    {
        var layout = Full();

        // Referenzhandbuch Tabelle 4-2: zwei Low, zwei Mid; RWW-Partition 0..3,
        // Blocknummer je Typ von vorn.
        Assert.Equal(FlashBlockType.Low, layout.PartitionAt(0x800000)!.Type);
        Assert.Equal(FlashBlockType.Low, layout.PartitionAt(0x810000)!.Type);
        Assert.Equal(FlashBlockType.Mid, layout.PartitionAt(0x820000)!.Type);
        Assert.Equal(FlashBlockType.Mid, layout.PartitionAt(0x830000)!.Type);

        Assert.Equal(0, layout.PartitionAt(0x800000)!.RwwPartition);
        Assert.Equal(3, layout.PartitionAt(0x830000)!.RwwPartition);

        Assert.Equal("Low-Block 1", layout.PartitionAt(0x810000)!.Label);
        Assert.Equal("Mid-Block 0", layout.PartitionAt(0x820000)!.Label);
    }

    [Fact]
    public void CseHighBlocks_AreNotPartOfTheContainer()
    {
        // Mit den beiden 16-KiB-CSE-Blöcken wäre die Datei 0x84C000 groß.
        // Diese Größe gehört zu keinem bekannten Layout.
        Assert.All(Full().Partitions, p => Assert.NotEqual("CSE", p.Label));
        Assert.False(Mpc5777cLayout.For(EcuFamily.Ems24, 0x84C000)!.Complete);
    }

    [Fact]
    public void Mpc5777cLayout_RejectsForeignFamilyAndSize()
    {
        Assert.Null(Mpc5777cLayout.For(EcuFamily.Ems23, 0x400000));
        Assert.Null(Mpc5777cLayout.For(EcuFamily.Ems24, 0x600000));   // kleiner als der Large Flash
    }

    [Fact]
    public void UnknownAppendix_ClaimsNoCpuAddress()
    {
        var layout = Mpc5777cLayout.For(EcuFamily.Ems24, 0x820000);

        Assert.NotNull(layout);
        Assert.False(layout!.Complete);
        Assert.Null(layout.ToCpu(0x810000));             // Anhang bleibt unzugeordnet
        Assert.Equal(0xF40000L, layout.ToCpu(0x740000)); // der Large Flash aber nicht
    }

    [Fact]
    public void LowMidErasedPartition_IsReportedAsErased()
    {
        var dump = FlashDump.FromBytes(TestDump.Image(Mpc5777cLayout.ContainerSize));

        var lowMid = dump.Partitions
                         .Where(p => p.Partition.Type is FlashBlockType.Low or FlashBlockType.Mid)
                         .ToList();

        Assert.Equal(4, lowMid.Count);
        Assert.All(lowMid, p => Assert.Equal(PartitionState.Erased, p.State));
    }

    [Fact]
    public void UtestPartition_IsDistinctFromEepromEmulation()
    {
        var dump = FlashDump.FromBytes(TestDump.Image(Mpc5777cLayout.ContainerSize));

        var utest = Assert.Single(dump.Partitions, p => p.Partition.Type == FlashBlockType.Utest);

        // Vollständig 0xFF, aber UTEST trägt ab Werk Sensorkalibrierung und
        // Chip-Kennung — leer kann er nicht sein, also wurde er nicht ausgelesen.
        Assert.Equal(PartitionState.NotRead, utest.State);
        Assert.Equal("nicht ausgelesen", utest.StateText);
        Assert.NotEqual(PartitionState.Erased, utest.State);
    }

    [Fact]
    public void OccupiedPartition_NamesWhatItHolds()
    {
        var image = TestDump.Image(Mpc5777cLayout.ContainerSize,
                                   (0x810000, TestDump.VolvoEcu(0x010000, "23310625P01", 0x4000)));
        var dump = FlashDump.FromBytes(image);

        var block = Assert.Single(dump.Partitions, p => p.Label == "Low-Block 1");

        Assert.Equal(PartitionState.Occupied, block.State);
        Assert.Contains("VOLVOECU", block.Description);
    }
}
