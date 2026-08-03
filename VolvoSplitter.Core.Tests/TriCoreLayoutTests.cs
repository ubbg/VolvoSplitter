using VolvoSplitter.Core;
using VolvoSplitter.Core.TriCore;

namespace VolvoSplitter.Core.Tests;

public class TriCoreLayoutTests
{
    private const long Bank = 0x200000;

    [Fact]
    public void Tc1796SectorMap_AccountsForEveryByte()
    {
        // 8 × 16 KiB + 1 × 128 KiB + 1 × 256 KiB + 3 × 512 KiB = 2048 KiB.
        var bank = Assert.Single(TriCoreDevice.Tc1796.Program);

        Assert.Equal(Bank, bank.Size);
        Assert.Equal(Bank, bank.SectorSum);
        Assert.Equal(13, bank.SectorSizes.Count);
        Assert.Equal(8, bank.SectorSizes.Count(s => s == 16 * 1024));
        Assert.Equal(3, bank.SectorSizes.Count(s => s == 512 * 1024));
    }

    [Fact]
    public void Tc1797SectorMap_AccountsForEveryByte()
    {
        // 8 × 16 KiB + 1 × 128 KiB + 7 × 256 KiB = 2048 KiB, für beide PMU.
        Assert.Equal(2, TriCoreDevice.Tc1797.Program.Count);

        Assert.All(TriCoreDevice.Tc1797.Program, bank =>
        {
            Assert.Equal(Bank, bank.Size);
            Assert.Equal(Bank, bank.SectorSum);
            Assert.Equal(16, bank.SectorSizes.Count);
            Assert.Equal(7, bank.SectorSizes.Count(s => s == 256 * 1024));
        });

        Assert.Equal(0x80000000, TriCoreDevice.Tc1797.Program[0].CpuStart);
        Assert.Equal(0x80800000, TriCoreDevice.Tc1797.Program[1].CpuStart);
    }

    [Fact]
    public void CachedAndUncachedAddresses_MapToTheSameOffset()
    {
        // 0x8… gecacht, 0xA… ungecacht — derselbe Flash. Die Infineon-
        // Unterlagen nennen 0xA…, die Bosch-Blockköpfe im Abbild 0x8….
        var layout = TriCoreLayout.For(TriCoreDevice.Tc1797, 0x400000);

        Assert.Equal(0x020000, layout.ToFile(0x80020000));
        Assert.Equal(0x020000, layout.ToFile(0xA0020000));
        Assert.Equal(0x200000, layout.ToFile(0x80800000));
        Assert.Equal(0x200000, layout.ToFile(0xA0800000));
    }

    [Fact]
    public void FourMegabyteImage_MapsBothBanks()
    {
        var layout = TriCoreLayout.For(TriCoreDevice.Tc1797, 0x400000);

        Assert.True(layout.Complete);
        Assert.Equal(2, layout.Partitions.Count);
        Assert.Equal("PMU0", layout.PartitionAt(0)!.Label);
        Assert.Equal("PMU1", layout.PartitionAt(0x200000)!.Label);

        // Die feine Sektorkarte dient nur der Anzeige, nicht der Zuordnung.
        Assert.Equal(32, layout.EraseSectors.Count);
        Assert.True(layout.IsEraseSectorStart(0x40000));
        Assert.False(layout.IsEraseSectorStart(0x40001));
    }

    [Fact]
    public void EightMegabyteImage_MapsExternalFlashBehindBothBanks()
    {
        // Die Form des ausgewerteten MED17.1-Abbilds: 4 MiB intern, 4 MiB extern.
        var layout = TriCoreLayout.For(TriCoreDevice.Tc1797, 0x800000);

        Assert.True(layout.Complete);
        Assert.Equal(3, layout.Partitions.Count);

        var external = layout.Partitions[2];
        Assert.Equal("Externer Flash (EBU)", external.Label);
        Assert.Equal(0x84000000, external.CpuStart);
        Assert.Equal(0x400000, external.FileLength);
        Assert.False(external.EmulatedEeprom);
        Assert.Contains("belegt", external.ExampleUse);
    }

    [Fact]
    public void ThreeMegabyteVariant_LeavesTheShortenedBankUngrouped()
    {
        // Das Datenblatt nennt für die 3-MiB-Ausführung keine Sektoraufteilung
        // der dann 1 MiB großen PMU1. Der Fall wird benannt, nicht gefüllt.
        var layout = TriCoreLayout.For(TriCoreDevice.Tc1797, 0x300000);

        Assert.False(layout.Complete);
        Assert.Equal(2, layout.Partitions.Count);
        Assert.Equal(0x100000, layout.Partitions[1].FileLength);
        Assert.Contains("keine Sektoraufteilung dokumentiert", layout.Partitions[1].ExampleUse);

        // Nur PMU0 bringt Löschsektoren mit.
        Assert.Equal(16, layout.EraseSectors.Count);
    }

    [Fact]
    public void DflashTail_OfExactSize_IsMappedAsEmulatedEeprom()
    {
        var layout = TriCoreLayout.For(TriCoreDevice.Tc1797, 0x400000 + 0x10000);

        Assert.True(layout.Complete);
        var dflash = Assert.Single(layout.Partitions, p => p.Type == FlashBlockType.DataFlash);

        Assert.Equal(0xAF000000, dflash.CpuStart);
        Assert.True(dflash.EmulatedEeprom);
    }

    [Fact]
    public void TriCoreDflashTail_IsNotAutomaticallyEeprom()
    {
        // Gegenstück zu RegionBeyondLargeFlash_IsNotAutomaticallyEeprom: ein
        // Anhang beliebiger Größe ist kein Datenflash, nur weil er hinten steht.
        var layout = TriCoreLayout.For(TriCoreDevice.Tc1797, 0x400000 + 0x8000);

        Assert.False(layout.Complete);
        Assert.DoesNotContain(layout.Partitions, p => p.EmulatedEeprom);
        Assert.DoesNotContain(layout.Partitions, p => p.Type == FlashBlockType.DataFlash);

        var tail = layout.Partitions[^1];
        Assert.Equal("Anhang", tail.Label);
        Assert.Null(tail.CpuStart);
        Assert.Null(layout.ToCpu(0x400000));
    }

    [Fact]
    public void UnknownTriCoreDevice_ClaimsNoSectorMap()
    {
        var device = TriCoreDevice.Generic(0x200000);
        var layout = TriCoreLayout.For(device, 0x200000);

        Assert.False(device.SectorMapKnown);
        Assert.False(layout.Complete);
        Assert.Empty(layout.EraseSectors);
        Assert.Contains("nicht hinterlegt", layout.SourceNote);

        // Die Adressen stimmen trotzdem — nur die Gliederung fehlt.
        Assert.Equal(0x80000000, layout.ToCpu(0));
        Assert.Equal(0x001000, layout.ToFile(0x80001000));
    }

    [Fact]
    public void NamedTriCoreWithoutDatasheet_IsNamedButNotMapped()
    {
        // TC1762/66/67/82/91/93 werden erkannt und benannt — mehr nicht.
        foreach (string name in TriCoreDevice.NamedWithoutSectorMap)
        {
            var device = TriCoreDevice.ByName(name);

            Assert.NotNull(device);
            Assert.Equal(name, device!.Name);
            Assert.False(device.SectorMapKnown);
            Assert.Contains("nicht hinterlegt", device.SourceNote);
        }
    }

    [Fact]
    public void ByName_ResolvesTheTwoMappedDevices()
    {
        Assert.Same(TriCoreDevice.Tc1796, TriCoreDevice.ByName("TC1796"));
        Assert.Same(TriCoreDevice.Tc1797, TriCoreDevice.ByName("Infineon TC1797 (4 MB)"));
        Assert.Null(TriCoreDevice.ByName("MPC5777C"));
        Assert.Null(TriCoreDevice.ByName(null));
    }

    [Fact]
    public void SourceNote_NamesWhereTheMapComesFrom()
    {
        Assert.Contains("Infineon-Datenblatt", TriCoreLayout.For(TriCoreDevice.Tc1797, 0x400000).SourceNote);
        Assert.Contains("Referenzhandbuch",
                        Mpc5777cLayout.For(EcuFamily.Ems24, Mpc5777cLayout.ContainerSize)!.SourceNote);
    }
}
