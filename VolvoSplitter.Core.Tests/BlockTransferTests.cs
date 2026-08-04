using VolvoSplitter.Core;
using VolvoSplitter.Core.TriCore;

namespace VolvoSplitter.Core.Tests;

/// <summary>
/// Der Blockübertrag 1:1 zwischen zwei Abbildern. Die wichtigsten Zusicherungen
/// sind nicht die, dass er funktioniert, sondern die, dass er nichts anderes
/// tut: kein Adressfeld wird umgeschrieben, keine Prüfsumme gestellt, kein Byte
/// außerhalb des Zielblocks angefasst.
/// </summary>
public class BlockTransferTests
{
    private const string UnitA = "10SW008917";
    private const string UnitB = "10SW026798";

    /// <summary>
    /// Ein MED17-ähnliches Abbild mit einstellbarer Kennung des Dataset-Blocks,
    /// damit Quelle und Ziel sich unterscheiden. Aufbau wie
    /// <see cref="TriCoreDump.SampleMed17Image"/>.
    /// </summary>
    private static byte[] Image(string datasetUnit = "10SW028601", byte datasetId = 0x60)
    {
        var customer = TriCoreDump.Block(0x30, 0x80000000, 0xFD04, 0x80020000, UnitA);
        var custTune = TriCoreDump.Block(0x90, 0x80010000, 0x2000, 0x80000000, UnitA,
                                         table2: [0x80011000]);
        var tuning = TriCoreDump.Block(0x20, 0x80014000, 0x3F00, 0x80010000, UnitA,
                                       extraFlags: TriCoreDump.OtpFlag);
        var startup = TriCoreDump.Block(0x10, 0x80018000, 0x7F00, 0x80014000, UnitA);
        var asw0 = TriCoreDump.Block(0x40, 0x80020000, 0x1E0000, 0x80800000, UnitB,
                                     algorithms: [0x00, 0x01, 0x10, 0x00]);
        var asw1 = TriCoreDump.Block(0x50, 0x80800000, 0x200000, 0x84100000, UnitB);
        var asw2 = TriCoreDump.Block(0xA0, 0x84100000, 0x280000, 0x84002000, UnitB);
        var dataset = TriCoreDump.Block(datasetId, 0x84002000, 0xFE000, 0, datasetUnit,
                                        variant: "34/1/MED17.1.6/5/P643//C643X5L8///");

        var pmu0 = TriCoreDump.Pflash(0x200000,
            (0x000000, customer), (0x010000, custTune), (0x014000, tuning),
            (0x018000, startup), (0x020000, asw0));
        var pmu1 = TriCoreDump.Pflash(0x200000, (0x000000, asw1));
        var external = TriCoreDump.Pflash(0x400000, (0x002000, dataset), (0x100000, asw2));

        return TriCoreDump.Tc1797Container(pmu0, pmu1, external);
    }

    private static FlashDump Dump(byte[] data, string name = "med17.bin") =>
        FlashDump.FromBytes(data, name);

    /// <summary>Der Dataset-Block — in beiden Abbildern bei CPU 0x84002000.</summary>
    private static SectorInfo Dataset(FlashDump dump) =>
        dump.Sectors.Single(s => s.CpuOffset == 0x84002000);

    // ==================================================================
    // Der Übertrag selbst
    // ==================================================================

    [Fact]
    public void Transfer_CopiesBlockByteForByte()
    {
        var source = Dump(Image("10SW099999"), "quelle.bin");
        var target = Dump(Image(), "ziel.bin");

        byte[] expected = source.SectorBytes(Dataset(source));

        var plan = BlockTransfer.Prepare(source, Dataset(source), target);
        Assert.True(plan.Possible, plan.RejectionText);

        var result = target.CopyBlockFrom(plan);

        Assert.Equal(expected.Length, result.BytesWritten);
        Assert.True(result.HeaderStillValid);

        var copied = Dataset(target);
        Assert.Equal(expected, target.SectorBytes(copied));
        Assert.Equal("10SW099999", copied.PartNumber);
    }

    [Fact]
    public void Transfer_TouchesNothingOutsideTheBlock()
    {
        var source = Dump(Image("10SW099999"), "quelle.bin");
        var target = Dump(Image(), "ziel.bin");

        byte[] before = target.Raw.ToArray();
        var block = Dataset(target);

        target.CopyBlockFrom(BlockTransfer.Prepare(source, Dataset(source), target));

        byte[] after = target.Raw.ToArray();
        Assert.Equal(before.Length, after.Length);

        // Der schärfste Zaun: er weiß nichts über Absichten. Ein einziger
        // Schreibvorgang für CVN, Prüfwertkopie oder Zeigerausbesserung lässt
        // diesen Test fallen.
        for (long i = 0; i < before.LongLength; i++)
            if (before[i] != after[i])
                Assert.InRange(i, block.Start, block.End - 1);

        // Und es hat sich überhaupt etwas geändert — sonst prüfte die Schleife nichts.
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Transfer_LeavesAddressFieldsUntouched()
    {
        var source = Dump(Image("10SW099999"), "quelle.bin");
        var target = Dump(Image(), "ziel.bin");

        var origin = source.Chain.Blocks.Single(b => b.CpuStart == 0x84002000);

        target.CopyBlockFrom(BlockTransfer.Prepare(source, Dataset(source), target));

        var copied = target.Chain.Blocks.Single(b => b.CpuStart == 0x84002000);

        // Die absoluten CPU-Adressen im Kopf werden mitgenommen, nicht
        // nachgezogen — genau deshalb geht der Übertrag nur an dieselbe Adresse.
        Assert.Equal(origin.CpuEnd, copied.CpuEnd);
        Assert.Equal(origin.NextCpu, copied.NextCpu);
        Assert.Equal(origin.Table1Cpu, copied.Table1Cpu);
        Assert.Equal(origin.Table2Cpu, copied.Table2Cpu);
        Assert.Equal(origin.ChecksumAdjust, copied.ChecksumAdjust);
        Assert.Equal(origin.Size, copied.Size);
    }

    [Fact]
    public void Transfer_KeepsHeaderValidAtDifferentFileOffset()
    {
        // Ziel ist ein reiner PMU1-Abzug: derselbe Block, dieselbe CPU-Adresse,
        // aber Datei-Offset 0 statt 0x200000. Der Normalfall im Bestand.
        var source = Dump(Image(), "voll.bin");
        var target = Dump(TriCoreDump.Pflash(0x200000,
            (0x000000, TriCoreDump.Block(0x50, 0x80800000, 0x200000, 0x84100000, "10SW111111"))),
            "pmu1.bin");

        var origin = source.Sectors.Single(s => s.CpuOffset == 0x80800000);
        var hit = target.Sectors.Single(s => s.CpuOffset == 0x80800000);

        Assert.NotEqual(origin.Start, hit.Start);

        var plan = BlockTransfer.Prepare(source, origin, target);
        Assert.True(plan.Possible, plan.RejectionText);

        // Verschiedene Offsets sind eine Feststellung, keine Warnung.
        var note = Assert.Single(plan.Findings, f => f.Rule == TransferRule.FileOffsetDiffers);
        Assert.Equal(TransferSeverity.Observation, note.Severity);

        var result = target.CopyBlockFrom(plan);

        Assert.True(result.HeaderStillValid);
        Assert.Equal("10SW026798", target.Sectors.Single(s => s.CpuOffset == 0x80800000).PartNumber);
    }

    [Fact]
    public void Transfer_StampsNoBoschChecksum()
    {
        // Ein Byte im geprüften Bereich der Quelle verfälschen: der Block geht
        // dort nicht mehr auf. Nach dem Übertrag geht er im Ziel ebenso wenig
        // auf — es wird nichts gestellt, auch nicht hilfsbereit.
        byte[] tampered = Image("10SW099999");
        tampered[0x402000 + 0x400] ^= 0xFF;

        var source = Dump(tampered, "quelle.bin");
        var target = Dump(Image(), "ziel.bin");

        Assert.Equal(SectorStatus.CrcMismatch, Dataset(source).Status);
        Assert.Equal(SectorStatus.Verified, Dataset(target).Status);

        var plan = BlockTransfer.Prepare(source, Dataset(source), target);
        Assert.True(plan.Possible, plan.RejectionText);
        Assert.Contains(plan.Findings, f => f.Rule == TransferRule.SourceChecksumNotVerified);

        var result = target.CopyBlockFrom(plan);

        Assert.Equal(SectorStatus.CrcMismatch, result.StatusAfter);
        Assert.Equal(SectorStatus.CrcMismatch, Dataset(target).Status);
    }

    [Fact]
    public void Transfer_ReportsChecksumsThatChanged()
    {
        byte[] tampered = Image("10SW099999");
        tampered[0x402000 + 0x400] ^= 0xFF;

        var source = Dump(tampered, "quelle.bin");
        var target = Dump(Image(), "ziel.bin");

        var result = target.CopyBlockFrom(BlockTransfer.Prepare(source, Dataset(source), target));

        // Vorher ging die Struktur des Dataset-Blocks auf, nachher nicht mehr.
        // Gemessen an zwei Momentaufnahmen, nicht aus der Absicht gefolgert.
        var broken = Assert.Single(result.Broken);
        Assert.Equal(Dataset(target).Start, broken.BlockStart);
        Assert.True(broken.Before);
        Assert.NotEqual(true, broken.After);
    }

    [Fact]
    public void Transfer_ShorterBlockErasesTailAndReportsIt()
    {
        // Quelle trägt an derselben Adresse einen kleineren Dataset-Block.
        var small = TriCoreDump.Block(0x60, 0x84002000, 0x8000, 0, "10SW099999");
        var source = Dump(TriCoreDump.Tc1797Container(
            TriCoreDump.Pflash(0x200000,
                (0x000000, TriCoreDump.Block(0x30, 0x80000000, 0xFD04, 0x84002000, UnitA))),
            TriCoreDump.Pflash(0x200000),
            TriCoreDump.Pflash(0x400000, (0x002000, small))), "quelle.bin");

        var target = Dump(Image(), "ziel.bin");

        long oldEnd = Dataset(target).End;
        var plan = BlockTransfer.Prepare(source, Dataset(source), target);

        Assert.True(plan.Possible, plan.RejectionText);
        Assert.Equal(0xFE000 - 0x8000, plan.TailErased);
        Assert.Contains(plan.Findings, f => f.Rule == TransferRule.SizeSmaller);

        var result = target.CopyBlockFrom(plan);

        Assert.Equal(0xFE000 - 0x8000, result.TailErased);
        Assert.True(target.IsErased(0x402000 + 0x8000, oldEnd - (0x402000 + 0x8000)));
    }

    // ==================================================================
    // Ablehnungen
    // ==================================================================

    [Fact]
    public void Transfer_RefusesSameImage()
    {
        var dump = Dump(Image());
        var plan = BlockTransfer.Prepare(dump, Dataset(dump), dump);

        Assert.False(plan.Possible);
        Assert.Contains(plan.Rejections, f => f.Rule == TransferRule.SameImage);
    }

    [Fact]
    public void Transfer_RefusesWhenTargetHasNoBlockAtSameCpuAddress()
    {
        var source = Dump(Image(), "quelle.bin");
        var target = Dump(TriCoreDump.Pflash(0x200000,
            (0x000000, TriCoreDump.Block(0x50, 0x80800000, 0x200000, 0x84100000, UnitB))),
            "pmu1.bin");

        var plan = BlockTransfer.Prepare(source, Dataset(source), target);

        Assert.False(plan.Possible);
        var no = Assert.Single(plan.Rejections, f => f.Rule == TransferRule.NoCounterpart);

        // Die Ablehnung wird begründet, nicht bloß ausgesprochen.
        Assert.Contains("84002000", no.Text);
    }

    [Fact]
    public void Transfer_RefusesDifferentBlockKindAtSameAddress()
    {
        // 0x60 gegen 0x70 — beide fallen auf SectorKind.Dataset, sind aber
        // verschiedene Blockarten. Die grobe Art genügt hier nicht.
        var source = Dump(Image(datasetId: 0x70), "quelle.bin");
        var target = Dump(Image(), "ziel.bin");

        var plan = BlockTransfer.Prepare(source, Dataset(source), target);

        Assert.Equal(SectorKind.Dataset, Dataset(source).Kind);
        Assert.Equal(SectorKind.Dataset, Dataset(target).Kind);

        Assert.False(plan.Possible);
        Assert.Contains(plan.Rejections, f => f.Rule == TransferRule.KindMismatch);
    }

    [Fact]
    public void Transfer_RefusesOtpTargetUnlessAsked()
    {
        var source = Dump(Image(), "quelle.bin");
        var target = Dump(Image(), "ziel.bin");

        var origin = source.Sectors.Single(s => s.CpuOffset == 0x80014000);
        Assert.True(origin.Otp);

        var strict = BlockTransfer.Prepare(source, origin, target);
        Assert.False(strict.Possible);
        Assert.Contains(strict.Rejections, f => f.Rule == TransferRule.TargetIsOtp);

        // Ausdrücklich zugelassen: der Befund verschwindet nicht, er wird zur
        // Warnung. Wer ihn wegklickt, soll ihn trotzdem im Ergebnis wiederfinden.
        var allowed = BlockTransfer.Prepare(source, origin, target, acceptOtpTarget: true);
        Assert.True(allowed.Possible, allowed.RejectionText);
        var note = Assert.Single(allowed.Findings, f => f.Rule == TransferRule.TargetIsOtp);
        Assert.Equal(TransferSeverity.Warning, note.Severity);
    }

    [Fact]
    public void Transfer_RefusesAcrossContainerKinds()
    {
        var source = Dump(Image(), "med17.bin");
        var target = FlashDump.FromBytes(
            TestDump.Image(Mpc5777cLayout.ContainerSize,
                (0x740000, TestDump.Sector(0xF40000, "23310644P02", 0x2000))), "ems24.mpc");

        var plan = BlockTransfer.Prepare(source, Dataset(source), target);

        Assert.False(plan.Possible);
        Assert.Contains(plan.Rejections, f => f.Rule == TransferRule.ContainerMismatch);
    }

    [Fact]
    public void Transfer_WritesNothingWhenPlanIsRejected()
    {
        var source = Dump(Image(datasetId: 0x70), "quelle.bin");
        var target = Dump(Image(), "ziel.bin");

        byte[] before = target.Raw.ToArray();
        var plan = BlockTransfer.Prepare(source, Dataset(source), target);

        Assert.Throws<InvalidOperationException>(() => target.CopyBlockFrom(plan));

        Assert.False(target.IsModified);
        Assert.Equal(before, target.Raw.ToArray());
    }

    [Fact]
    public void Transfer_RefusesPlanOfAnotherTarget()
    {
        var source = Dump(Image("10SW099999"), "quelle.bin");
        var target = Dump(Image(), "ziel.bin");
        var stranger = Dump(Image(), "dritte.bin");

        var plan = BlockTransfer.Prepare(source, Dataset(source), target);

        Assert.Throws<ArgumentException>(() => stranger.CopyBlockFrom(plan));
        Assert.False(stranger.IsModified);
    }

    [Fact]
    public void Transfer_IntoUnknownProfile_IsRejectedBeforeAnyWrite()
    {
        // Ohne erkannten Container gibt es im Ziel keinen Block, sondern nur
        // Bytes an einem Offset. Die Ablehnung fällt schon in der Prüfung —
        // EnsureBlockTransfer im Schreibpfad ist die zweite Reihe, die von außen
        // gar nicht mehr erreichbar ist, weil es ohne Prüfung keinen Plan gibt.
        var source = Dump(Image(), "quelle.bin");
        var target = FlashDump.FromBytes(new byte[0x20000], "eeprom.bin");

        Assert.False(target.Profile.SupportsBlockTransfer);

        var plan = BlockTransfer.Prepare(source, Dataset(source), target);

        Assert.False(plan.Possible);
        Assert.Contains(plan.Rejections, f => f.Rule == TransferRule.ContainerMismatch);
        Assert.Throws<InvalidOperationException>(() => target.CopyBlockFrom(plan));
        Assert.False(target.IsModified);
    }

    // ==================================================================
    // Warnungen, die den Übertrag nicht verhindern
    // ==================================================================

    [Fact]
    public void Transfer_ReportsDifferentIdentifier()
    {
        var source = Dump(Image("10SW099999"), "quelle.bin");
        var target = Dump(Image(), "ziel.bin");

        var plan = BlockTransfer.Prepare(source, Dataset(source), target);

        Assert.True(plan.Possible, plan.RejectionText);
        var note = Assert.Single(plan.Findings, f => f.Rule == TransferRule.IdentifierDiffers);
        Assert.Equal(TransferSeverity.Warning, note.Severity);
    }

    // ==================================================================
    // TRW — derselbe Vorgang, dasselbe Versprechen
    // ==================================================================

    private static byte[] Ems24(string partNumber) =>
        TestDump.Image(Mpc5777cLayout.ContainerSize,
            (0x740000, TestDump.Sector(0xF40000, partNumber, 0x40000)),
            (0x7C0000, TestDump.Sector(0xFC0000, "23310699P03", 0x20000)));

    [Fact]
    public void Transfer_TrwSector_CopiesWithoutStampingCrc()
    {
        var source = FlashDump.FromBytes(Ems24("23310644P09"), "quelle.mpc");
        var target = FlashDump.FromBytes(Ems24("23310644P02"), "ziel.mpc");

        var origin = source.Sectors.Single(s => s.Kind == SectorKind.Calibration);
        var plan = BlockTransfer.Prepare(source, origin, target);

        Assert.True(plan.Possible, plan.RejectionText);

        var result = target.CopyBlockFrom(plan);
        var copied = target.Sectors.Single(s => s.Kind == SectorKind.Calibration);

        Assert.True(result.HeaderStillValid);
        Assert.Equal("23310644P09", copied.PartNumber);
        Assert.Equal(source.SectorBytes(origin), target.SectorBytes(copied));
    }

    [Fact]
    public void Transfer_TrwSector_KeepsAMismatchAMismatch()
    {
        // Verfälschte Quelle: die Prüfsumme weicht ab und wird nicht gestellt.
        byte[] tampered = Ems24("23310644P09");
        tampered[0x740000 + 0x200] ^= 0xFF;

        var source = FlashDump.FromBytes(tampered, "quelle.mpc");
        var target = FlashDump.FromBytes(Ems24("23310644P02"), "ziel.mpc");

        var origin = source.Sectors.Single(s => s.Kind == SectorKind.Calibration);
        Assert.Equal(SectorStatus.CrcMismatch, origin.Status);

        var result = target.CopyBlockFrom(BlockTransfer.Prepare(source, origin, target));

        Assert.Equal(SectorStatus.CrcMismatch, result.StatusAfter);
    }

    [Fact]
    public void Transfer_TrwSector_RefusesWhenTheRoleIsMissingInTheTarget()
    {
        // Quelle trägt zusätzlich einen ASW-Sektor, das Ziel nicht. Für ihn gibt
        // es im Ziel keine Adresse — und damit keinen Übertrag. Der Vorgang
        // sucht ein Gegenstück; er sucht sich keinen Platz.
        var source = FlashDump.FromBytes(TestDump.Image(Mpc5777cLayout.ContainerSize,
            (0x200000, TestDump.Sector(0xA00000, "23310625P01", 0x40000)),
            (0x740000, TestDump.Sector(0xF40000, "23310644P09", 0x40000))), "quelle.mpc");

        var target = FlashDump.FromBytes(Ems24("23310644P02"), "ziel.mpc");

        var asw = source.Sectors.Single(s => s.Kind == SectorKind.Asw && s.Present);
        Assert.DoesNotContain(target.Sectors, s => s.Kind == SectorKind.Asw && s.Present);

        var plan = BlockTransfer.Prepare(source, asw, target);

        Assert.False(plan.Possible);
        Assert.Contains(plan.Rejections, f => f.Rule == TransferRule.NoCounterpart);
    }
}
