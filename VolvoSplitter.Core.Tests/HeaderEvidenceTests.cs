using VolvoSplitter.Core;
using VolvoSplitter.Core.TriCore;

namespace VolvoSplitter.Core.Tests;

/// <summary>
/// Drei Befunde aus einer Abnahme an 1516 echten VAG-EDC17-Abbildern, die alle
/// dieselbe Form haben: das Werkzeug verwarf etwas, ohne es zu sagen.
///
/// * Der <strong>Nullpunkt</strong> des Abbilds war gesetzt statt gemessen.
///   227 Abbilder verloren dadurch sämtliche Blöcke, obwohl jeder ihrer
///   Blockköpfe seine Lage selbst nennt.
/// * Ein <strong>Kennungsfeld</strong>, das nicht als Text lesbar war, verwarf
///   den ganzen Kopf — 25 Abbilder, 123 Blöcke.
/// * Die <strong>Belegliste</strong> der Blockkette wurde gerechnet und
///   weggeworfen. In keinem der Berichte stand, <em>warum</em> es null Blöcke
///   sind.
/// </summary>
public class HeaderEvidenceTests
{
    private const long HalfMib = 0x80000;
    private const long TwoMib = 0x200000;
    private const long FourMib = 0x400000;

    /// <summary>
    /// Eine Teilauslesung, wie sie im Bestand der Normalfall ist: 512 KiB ab
    /// CPU 0x80180000, zwei Blöcke, der zweite verweist über <c>nextSector</c>
    /// auf den ersten. Nachgebaut nach den Bytes von
    /// <c>EDC17CP04/Audi_Original_4L0910401G_0281014174_0070_Original_240PS.bin</c>:
    ///
    ///     @0x000000 id=0x80 size=0x1FF04 blockEnd=0x8019FF00 next=0x00000000
    ///     @0x020000 id=0x60 size=0x5FF04 blockEnd=0x801FFF00 next=0x80180000
    /// </summary>
    private static byte[] PartialReadImage() => TriCoreDump.Pflash(HalfMib,
        (0x000000, TriCoreDump.Block(0x80, 0x80180000, 0x1FF04, 0, "10SW008917")),
        (0x020000, TriCoreDump.Block(0x60, 0x801A0000, 0x5FF04, 0x80180000, "10SW008917")));

    // ==================================================================
    // Der Nullpunkt wird gemessen, nicht gesetzt
    // ==================================================================

    [Fact]
    public void PartialRead_IsReadFromTheBaseItsHeadersName()
    {
        var dump = FlashDump.FromBytes(PartialReadImage(), "teilauslesung.bin");

        Assert.Equal(2, dump.Chain.Blocks.Count);
        Assert.All(dump.Sectors, s => Assert.Equal(SectorStatus.Verified, s.Status));

        // Der Nullpunkt steht nicht mehr auf der PFLASH-Basis, sondern dort,
        // wo die Blockköpfe ihn hinlegen.
        Assert.Equal(0x80180000, dump.Chain.Blocks[0].CpuStart);
        Assert.Equal(0x801A0000, dump.Chain.Blocks[1].CpuStart);
    }

    [Fact]
    public void PartialRead_ReportsTheCpuRangeItActuallyCovers()
    {
        // Nebenwirkung desselben Fehlers: der Bericht wies 0x80000000–0x80080000
        // aus — eine Aussage, der der Blockkopf im selben Abbild widersprach.
        var dump = FlashDump.FromBytes(PartialReadImage(), "teilauslesung.bin");

        Assert.Equal(0x80180000, dump.Layout!.ToCpu(0));
        Assert.Equal(0, dump.Layout.ToFile(0x80180000));

        var partition = Assert.Single(dump.Partitions);
        Assert.Equal(Hex.Range(0x80180000, 0x80200000), partition.CpuRange);
    }

    [Fact]
    public void MeasuredBase_IsNamedInTheEvidence()
    {
        // Eine Adresse, deren Herkunft niemand sieht, ist keine Messung.
        var dump = FlashDump.FromBytes(PartialReadImage(), "teilauslesung.bin");

        Assert.Contains(dump.Detection.Evidence,
                        e => e.Contains("Nullpunkt 0x80180000") && e.Contains("gemessen"));
    }

    [Fact]
    public void WindowStarts_AreMeasuredFromBlockEndMinusSize()
    {
        // blockStart = blockEnd − size + 4, also Nullpunkt = blockStart − fileStart.
        Assert.Equal([0x80180000], BoschBlockChain.MeasureWindowStarts(PartialReadImage()));

        // Ein Abbild, das an der PFLASH-Basis beginnt, misst genau das — und
        // die häufigste Basis steht vorn.
        var sample = BoschBlockChain.MeasureWindowStarts(TriCoreDump.SampleMed17Image());
        Assert.Equal(0x80000000, sample[0]);

        // Ohne Blockköpfe gibt es nichts zu messen — und keinen Ratewert.
        Assert.Empty(BoschBlockChain.MeasureWindowStarts(TriCoreDump.Pflash(TwoMib)));
    }

    [Fact]
    public void MeasuredBase_MustBeatTheTabulatedSplit_NotMerelyTieIt()
    {
        // Ein Abbild mit einem einzigen Block in PMU1. Die Bankaufteilung des
        // TC1797 bestätigt ihn (Datei 0x200000 → CPU 0x80800000), und der aus
        // demselben Kopf gemessene Nullpunkt 0x80600000 tut es auch — ein Kopf
        // bestätigt die aus ihm selbst abgeleitete Basis zwangsläufig.
        //
        // Stünden beide in einer Rangliste, machte dieser Gleichstand aus einem
        // klaren Vorsprung eine Mehrdeutigkeit, und das Abbild verlöre seinen
        // Block. Genau das ist beim Bau passiert: zwei EDC17CP74-Abbilder des
        // Bestands fielen von 1 Block auf 0.
        var image = TriCoreDump.Pflash(FourMib,
            (0x200000, TriCoreDump.Block(0x60, 0x80800000, 0x4000, 0, "10SW008917")));

        var dump = FlashDump.FromBytes(image, "nur_pmu1.bin");

        Assert.Equal("TC1797", dump.Profile.MicroName);
        Assert.Single(dump.Chain.Blocks);
        Assert.Equal(0x80800000, dump.Chain.Blocks[0].CpuStart);
        Assert.Equal(0x80000000, dump.Layout!.ToCpu(0));
    }

    [Fact]
    public void DefaultWindowStart_IsUnchanged()
    {
        // Der Vorgabefall darf sich nicht verschieben: ohne Angabe gilt
        // weiterhin der Anfang des Bausteins.
        Assert.Equal(0x80000000, TriCoreLayout.For(TriCoreDevice.Tc1796, TwoMib).ToCpu(0));
        Assert.Equal(0x80000000, TriCoreLayout.For(TriCoreDevice.Tc1797, FourMib).ToCpu(0));
        Assert.Equal(0x80800000, TriCoreLayout.For(TriCoreDevice.Tc1797, FourMib).ToCpu(TwoMib));
        Assert.Equal(0x80000000,
                     TriCoreLayout.For(TriCoreDevice.LinearProgramFlash, TwoMib).ToCpu(0));
    }

    [Fact]
    public void WindowStart_ShiftsTheWholeMapping()
    {
        var layout = TriCoreLayout.For(TriCoreDevice.Tc1796, HalfMib, 0x80180000);

        Assert.Equal(0x80180000, layout.ToCpu(0));
        Assert.Equal(0, layout.ToFile(0x80180000));
        Assert.Equal(HalfMib - 4, layout.ToFile(0x801FFFFC));

        // Angeschnittene Bank: keine Löschsektorkarte, und das wird gesagt.
        Assert.False(layout.Complete);
        Assert.Empty(layout.EraseSectors);
    }

    [Fact]
    public void WindowStartOutsideEveryBank_IsNotRoundedUpSilently()
    {
        // 0x80400000 liegt beim TC1797 zwischen PMU0 (endet 0x80200000) und
        // PMU1 (beginnt 0x80800000). Der Bankdurchlauf würde den Fensteranfang
        // stillschweigend auf PMU1 aufrunden und eine Adresse behaupten, die
        // niemand gemessen hat.
        var layout = TriCoreLayout.For(TriCoreDevice.Tc1797, TwoMib, 0x80400000);

        Assert.Equal(0x80400000, layout.ToCpu(0));
        var partition = Assert.Single(layout.Partitions);
        Assert.Contains("liegt in keiner Bank", partition.ExampleUse);
    }

    // ==================================================================
    // Ein unlesbares Kennungsfeld ist eine fehlende Kennung
    // ==================================================================

    /// <summary>Die zehn Bytes des Kennungsfelds eines Blocks überschreiben.</summary>
    private static void FillIdentifier(byte[] image, long blockStart, params byte[] bytes)
    {
        for (int i = 0; i < BoschBlockChain.SwIdentifierLength; i++)
            image[blockStart + BoschBlockChain.SwIdentifierOffset + i] = bytes[i % bytes.Length];
    }

    [Fact]
    public void IdentifierFilledWithAf_KeepsTheBlock()
    {
        // 25 Abbilder des Bestands füllen das Feld mit 0xAF statt 0x00/0xFF.
        // Die vier scharfen Regeln sind an dieser Stelle längst durch.
        var image = TriCoreDump.SampleMed17Image();
        FillIdentifier(image, 0x018000, 0xAF);

        var chain = BoschBlockChain.Read(image, TriCoreLayout.For(TriCoreDevice.Tc1797, 0x800000));

        Assert.Equal(8, chain.Blocks.Count);

        var startup = Assert.Single(chain.Blocks, b => b.Id == 0x10);
        Assert.Equal("", startup.Identifier);
        Assert.False(startup.ChecksumMismatch);

        // Die übrigen Kennungen bleiben unberührt.
        Assert.All(chain.Blocks.Where(b => b.Id != 0x10),
                   b => Assert.Matches(@"^\d{2}SW\d{6}$", b.Identifier));
    }

    [Fact]
    public void BlockWithoutIdentifier_StillGetsAnOutputName()
    {
        // Ohne Rückfallnamen hießen alle kennungslosen Blöcke gleich.
        var image = TriCoreDump.SampleMed17Image();
        FillIdentifier(image, 0x018000, 0xAF);

        var dump = FlashDump.FromBytes(image, "af_kennung.bin");

        Assert.Equal(8, dump.Sectors.Count);
        Assert.Equal(8, dump.Sectors.Select(s => s.OutputName).Distinct().Count());
        Assert.Contains(dump.Sectors, s => s.PartNumber == "0x10");
    }

    [Fact]
    public void PartlyUnreadableIdentifier_IsEmpty_NotHalfRead()
    {
        // Alles oder nichts: aus Binärrauschen den druckbaren Teil
        // herauszuklauben erfände eine Teilenummer, die es nicht gibt.
        var image = TriCoreDump.SampleMed17Image();
        FillIdentifier(image, 0x018000, (byte)'1', (byte)'0', (byte)'S', (byte)'W', 0xAF);

        var chain = BoschBlockChain.Read(image, TriCoreLayout.For(TriCoreDevice.Tc1797, 0x800000));

        Assert.Equal(8, chain.Blocks.Count);
        Assert.Equal("", Assert.Single(chain.Blocks, b => b.Id == 0x10).Identifier);
    }

    // ==================================================================
    // „Nichts drin" und „nicht verstanden" sind zwei Aussagen
    // ==================================================================

    [Fact]
    public void RejectedHeaderCandidates_ReachTheReport()
    {
        // Ein Kopf, der jede Regel erfüllt außer der siebten: seine
        // Zeigertabelle liegt außerhalb des eigenen Blocks. Er ist damit an
        // jedem Nullpunkt ein Kandidat und an keinem ein Block — genau der
        // Fall, in dem der Nutzer erfahren muss, dass hier etwas stand.
        var block = TriCoreDump.Block(0x10, 0x80018000, 0x2000, 0, "10SW008917",
                                      table2: [0xDEAD0001, 0xDEAD0002]);
        TestDump.WriteLe(block, BoschBlockChain.Table2PointerOffset, 0x80100000);

        var image = TriCoreDump.Pflash(TwoMib,
            (0x018000, block),
            (0x100000, TriCoreDump.CodeBlock(0x40000, 0x80000000)));

        var dump = FlashDump.FromBytes(image, "verworfen.bin");

        Assert.Empty(dump.Chain.Blocks);
        Assert.Contains(dump.Detection.Evidence, e => e.Contains("Blockkopfkandidat bei 0x018000"));
        Assert.Contains(dump.Detection.Evidence,
                        e => e.Contains("Kein bestätigter Bosch-Blockkopf gefunden"));
    }

    [Fact]
    public void BrokenChain_IsExplainedInTheReport()
    {
        // Der Kettenleser rechnet diese Sätze ohnehin; sie wurden nur nie
        // weitergereicht.
        var image = TriCoreDump.Pflash(TwoMib,
            (0x018000, TriCoreDump.Block(0x10, 0x80018000, 0x2000, 0x80100000, "10SW008917")));

        var dump = FlashDump.FromBytes(image, "kette_reisst.bin");

        Assert.Single(dump.Chain.Blocks);
        Assert.Contains(dump.Detection.Evidence, e => e.Contains("Kette bricht ab"));
    }

    [Fact]
    public void AgreementOfBothMethods_IsAlsoStatedInTheReport()
    {
        // Die Belegliste soll nicht nur erklären, was fehlschlug: dass
        // Abtastung und Kettenlauf dasselbe liefern, ist ein eigener Beleg.
        var dump = FlashDump.FromBytes(TriCoreDump.SampleMed17Image(), "med17.bin");

        Assert.Contains(dump.Detection.Evidence,
                        e => e.Contains("Abtastung und Kettenlauf liefern dieselben 8 Blöcke"));
    }
}
