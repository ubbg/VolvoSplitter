using System.Text;
using VolvoSplitter.Core;

namespace VolvoSplitter.Core.Tests;

public class FlashDumpTests
{
    // EMS2.3-Adressen: Parameter 0x060000, ASW 0x100000, Kalibrierung 0x380000.
    private const long Ems23Size = 0x400000;
    private const long ParamStart = 0x060000;

    [Fact]
    public void ReadSector_ValidSector_IsVerified()
    {
        var sector = TestDump.Sector(ParamStart, "31399478 AA", 0x2000);
        var image = TestDump.Image(Ems23Size, (ParamStart, sector));

        var dump = FlashDump.FromBytes(image, fixedAddressesOnly: true);
        var param = dump.Sectors.Single(s => s.Kind == SectorKind.Parameter);

        Assert.True(param.Present);
        Assert.Equal(SectorStatus.Verified, param.Status);
        Assert.Equal("31399478 AA", param.PartNumber);
        Assert.Equal(0x2000, param.Length);
        Assert.Equal(param.CrcStored, param.CrcComputed);
    }

    [Fact]
    public void ReadSector_CorruptedBody_IsCrcMismatch()
    {
        var sector = TestDump.Sector(ParamStart, "31399478 AA", 0x2000);
        var image = TestDump.Image(Ems23Size, (ParamStart, sector));

        // Ein Nutzdatenbyte kippen — der gespeicherte Prüfwert passt dann nicht mehr.
        image[ParamStart + 0x400] ^= 0xFF;

        var dump = FlashDump.FromBytes(image, fixedAddressesOnly: true);
        var param = dump.Sectors.Single(s => s.Kind == SectorKind.Parameter);

        Assert.Equal(SectorStatus.CrcMismatch, param.Status);
        Assert.NotEqual(param.CrcStored, param.CrcComputed);
    }

    [Fact]
    public void Discovery_FindsRelocatedSector()
    {
        // Sektor trägt den CPU-Offset der Parameter-Rolle, liegt aber woanders.
        const long placedAt = 0x070000;
        var sector = TestDump.Sector(ParamStart, "31399478 AA", 0x2000);
        var image = TestDump.Image(Ems23Size, (placedAt, sector));

        var dump = FlashDump.FromBytes(image, fixedAddressesOnly: false);
        var param = dump.Sectors.Single(s => s.Kind == SectorKind.Parameter);

        Assert.True(param.Present);
        Assert.True(param.Relocated);
        Assert.Equal(placedAt, param.Start);
        Assert.Equal(ParamStart, param.ExpectedStart);
    }

    [Fact]
    public void Discovery_MissingRole_StillReported()
    {
        var sector = TestDump.Sector(ParamStart, "31399478 AA", 0x2000);
        var image = TestDump.Image(Ems23Size, (ParamStart, sector));

        var dump = FlashDump.FromBytes(image, fixedAddressesOnly: false);

        // ASW und Kalibrierung fehlen, müssen aber als "nicht gefunden" erscheinen.
        Assert.Contains(dump.Sectors, s => s.Kind == SectorKind.Asw && !s.Present);
        Assert.Contains(dump.Sectors, s => s.Kind == SectorKind.Calibration && !s.Present);
    }

    [Fact]
    public void RepairCrc_TurnsMismatchIntoVerified()
    {
        var sector = TestDump.Sector(ParamStart, "31399478 AA", 0x2000);
        // Gespeicherten Prüfwert im Trailer verfälschen.
        TestDump.WriteBe(sector, sector.Length - FlashFormat.CrcTrailerLen, 0xDEADBEEF);
        var image = TestDump.Image(Ems23Size, (ParamStart, sector));

        var dump = FlashDump.FromBytes(image, fixedAddressesOnly: true);
        var param = dump.Sectors.Single(s => s.Kind == SectorKind.Parameter);
        Assert.Equal(SectorStatus.CrcMismatch, param.Status);

        var repair = dump.RepairCrc(param);
        Assert.Equal(0xDEADBEEFu, repair.OldCrc);

        dump.Analyze(fixedAddressesOnly: true);
        var fixedSector = dump.Sectors.Single(s => s.Kind == SectorKind.Parameter);
        Assert.Equal(SectorStatus.Verified, fixedSector.Status);
    }

    [Fact]
    public void FindChecksumCopies_LocatesDuplicateOfStoredCrc()
    {
        var sector = TestDump.Sector(ParamStart, "31399478 AA", 0x2000);
        var image = TestDump.Image(Ems23Size, (ParamStart, sector));

        var dump = FlashDump.FromBytes(image, fixedAddressesOnly: true);
        var param = dump.Sectors.Single(s => s.Kind == SectorKind.Parameter);

        // Denselben Prüfwert an eine freie Stelle schreiben und neu analysieren.
        const long copyAt = 0x200000;
        dump.PatchUInt32Be(copyAt, param.CrcStored);
        dump.Analyze(fixedAddressesOnly: true);

        var again = dump.Sectors.Single(s => s.Kind == SectorKind.Parameter);
        Assert.Contains(copyAt, again.ChecksumCopies);
    }

    [Fact]
    public void ReplaceSector_DifferentCpuOffset_IsReported()
    {
        var original = TestDump.Sector(ParamStart, "31399478 AA", 0x2000);
        var image = TestDump.Image(Ems23Size, (ParamStart, original));
        var dump = FlashDump.FromBytes(image, fixedAddressesOnly: true);
        var param = dump.Sectors.Single(s => s.Kind == SectorKind.Parameter);

        // Ersatz mit abweichendem CPU-Offset im eigenen Kopf.
        var replacement = TestDump.Sector(0x090000, "99999999 ZZ", 0x1000);
        var result = dump.ReplaceSector(param, replacement, repairCrc: true);

        Assert.True(result.HeaderFound);
        Assert.True(result.CpuOffsetChanged);
        Assert.Equal(0x090000, result.ActualCpuOffset);
    }

    [Fact]
    public void ReplaceSector_RawData_ReportsNoHeader()
    {
        var original = TestDump.Sector(ParamStart, "31399478 AA", 0x2000);
        var image = TestDump.Image(Ems23Size, (ParamStart, original));
        var dump = FlashDump.FromBytes(image, fixedAddressesOnly: true);
        var param = dump.Sectors.Single(s => s.Kind == SectorKind.Parameter);

        var raw = new byte[0x1000];
        Array.Fill(raw, (byte)0x5A);
        var result = dump.ReplaceSector(param, raw, repairCrc: true);

        Assert.False(result.HeaderFound);
    }

    [Fact]
    public void ReadVehicleInfo_ParsesVolvoIdentity()
    {
        var sector = TestDump.Sector(ParamStart, "31399478 AA", 0x2000, patchBody: data =>
        {
            Plant(data, FlashFormat.ChassisOffset, "B    904194");
            Plant(data, FlashFormat.MakerOffset, "VOLVO");
            Plant(data, FlashFormat.VinOffset, "YV1AB1234C5678901");
        });
        var image = TestDump.Image(Ems23Size, (ParamStart, sector));

        var dump = FlashDump.FromBytes(image, fixedAddressesOnly: true);

        Assert.NotNull(dump.Vehicle);
        Assert.Equal("YV1AB1234C5678901", dump.Vehicle!.Vin);
        Assert.Equal("VOLVO", dump.Vehicle.Maker);
        Assert.Equal("B 904194", dump.Vehicle.ChassisNumber);
    }

    private static void Plant(byte[] data, int offset, string text) =>
        Encoding.ASCII.GetBytes(text).CopyTo(data, offset);
}
