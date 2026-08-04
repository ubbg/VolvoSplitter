using VolvoSplitter.Core;
using VolvoSplitter.Core.TriCore;

namespace VolvoSplitter.Core.Tests;

public class BoschBlockChainTests
{
    private const long Pmu0Size = 0x200000;

    /// <summary>CPU-Adresse des üblichen Ketteneinstiegs, als Datei-Offset.</summary>
    private const long EntryFile = 0x018000;

    private static PhysicalLayout Layout(long imageSize) =>
        TriCoreLayout.For(TriCoreDevice.Tc1797, imageSize);

    private static byte[] SingleBlockImage(byte[] block, long at = EntryFile) =>
        TriCoreDump.Pflash(Pmu0Size, (at, block));

    // ==================================================================
    // Positivkontrolle: die Blockkarte des ausgewerteten Abbilds
    // ==================================================================

    [Fact]
    public void BlockChain_WalksTheSampleLayout()
    {
        var chain = BoschBlockChain.Read(TriCoreDump.SampleMed17Image(), Layout(0x800000));

        (long File, long Size, byte Id)[] expected =
        [
            (0x000000, 0x00FD04, 0x30),   // Customer
            (0x010000, 0x002000, 0x90),   // Customer Tuning protection
            (0x014000, 0x003F00, 0x20),   // Tuning protection
            (0x018000, 0x007F00, 0x10),   // Startup
            (0x020000, 0x1E0000, 0x40),   // ASW #0
            (0x200000, 0x200000, 0x50),   // ASW #1   — PMU1
            (0x402000, 0x0FE000, 0x60),   // Dataset #0 — externer Flash
            (0x500000, 0x280000, 0xA0)    // ASW #2   — externer Flash
        ];

        Assert.Equal(expected.Length, chain.Blocks.Count);

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].File, chain.Blocks[i].FileStart);
            Assert.Equal(expected[i].Size, chain.Blocks[i].Size);
            Assert.Equal(expected[i].Id, chain.Blocks[i].Id);
        }

        // Jeder Block hat nachgerechnete und stimmende Prüfsummen.
        Assert.All(chain.Blocks, b =>
        {
            Assert.True(b.ChecksumsComputed);
            Assert.False(b.ChecksumMismatch);
        });

        Assert.All(BoschBlockChain.ToSectors(chain.Blocks),
                   s => Assert.Equal(SectorStatus.Verified, s.Status));
    }

    [Fact]
    public void ChainCrossesBanksInTheObservedOrder()
    {
        var chain = BoschBlockChain.Read(TriCoreDump.SampleMed17Image(), Layout(0x800000));

        // Die Kette ist nicht adressgeordnet: sie beginnt bei 0x80018000 und
        // läuft rückwärts durch PMU0, dann nach PMU1 und in den externen Flash.
        long[] order = chain.ChainBlocks.Select(b => b.CpuStart).ToArray();

        Assert.Equal(
            [0x80018000, 0x80014000, 0x80010000, 0x80000000,
             0x80020000, 0x80800000, 0x84100000, 0x84002000],
            order);

        Assert.Null(chain.ChainBlocks[^1].NextCpu);   // Kettenende
    }

    [Fact]
    public void UnalignedBlockSize_IsAccepted()
    {
        // Block 4 ist 0xFD04 groß. Eine Prüfung auf Sektorbündigkeit wäre falsch.
        var chain = BoschBlockChain.Read(TriCoreDump.SampleMed17Image(), Layout(0x800000));

        var customer = Assert.Single(chain.Blocks, b => b.Id == 0x30);
        Assert.Equal(0xFD04, customer.Size);
        Assert.NotEqual(0, customer.Size % 0x1000);
    }

    [Fact]
    public void BlocksAreGroupedBySoftwareUnit()
    {
        var chain = BoschBlockChain.Read(TriCoreDump.SampleMed17Image(), Layout(0x800000));

        var units = chain.Blocks.GroupBy(b => b.Identifier)
                                .ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(3, units.Count);
        Assert.Equal(4, units["10SW008917"]);
        Assert.Equal(3, units["10SW026798"]);
        Assert.Equal(1, units["10SW028601"]);
    }

    [Fact]
    public void SwIdentifier_IsTenBytesFollowedByEightUnknown()
    {
        var chain = BoschBlockChain.Read(TriCoreDump.SampleMed17Image(), Layout(0x800000));
        var startup = Assert.Single(chain.Blocks, b => b.Id == 0x10);

        Assert.Equal("10SW008917", startup.Identifier);
        Assert.Matches(@"^\d{2}SW\d{6}$", startup.Identifier);

        // Die acht Bytes bei +0x24 werden roh ausgewiesen und nicht gedeutet.
        Assert.Equal(8, startup.UnknownBytes.Length);
        Assert.All(startup.UnknownBytes, b => Assert.Equal(0xFF, b));
    }

    [Fact]
    public void OtpFlag_IsReported()
    {
        var chain = BoschBlockChain.Read(TriCoreDump.SampleMed17Image(), Layout(0x800000));

        var tuning = Assert.Single(chain.Blocks, b => b.Id == 0x20);
        Assert.True(tuning.Otp);
        Assert.Equal(TriCoreDump.OtpFlag, tuning.Flags & TriCoreDump.OtpFlag);

        Assert.All(chain.Blocks.Where(b => b.Id != 0x20), b => Assert.False(b.Otp));
    }

    [Fact]
    public void OtpFlag_ReachesTheSectorAsAFieldNotAsText()
    {
        var chain = BoschBlockChain.Read(TriCoreDump.SampleMed17Image(), Layout(0x800000));
        var sectors = BoschBlockChain.ToSectors(chain.Blocks);

        var tuning = Assert.Single(sectors, s => s.Otp);

        // Das Kennzeichen steht im Modell, damit die Oberfläche es färben kann;
        // die Bezeichnung bleibt frei davon. Wer keine Farbe hat — Bericht und
        // Befehlszeile —, nimmt LabelText und bekommt es ausgeschrieben.
        Assert.DoesNotContain("OTP", tuning.Label);
        Assert.EndsWith(" · OTP", tuning.LabelText);

        Assert.All(sectors.Where(s => !s.Otp), s => Assert.Equal(s.Label, s.LabelText));
    }

    [Fact]
    public void BlockGaps_AreReportedWithoutInterpretation()
    {
        var chain = BoschBlockChain.Read(TriCoreDump.SampleMed17Image(), Layout(0x800000));

        (long From, long To)[] expected =
        [
            (0x00FD04, 0x010000),   //     764 B
            (0x012000, 0x014000),   //   8.192 B
            (0x017F00, 0x018000),   //     256 B
            (0x01FF00, 0x020000),   //     256 B
            (0x400000, 0x402000),   //   8.192 B
            (0x780000, 0x800000)    // 524.288 B
        ];

        Assert.Equal(expected, chain.Gaps);

        // Gemeldet wird, dass sie da sind — nicht, was drinsteht.
        string evidence = string.Join('\n', chain.Evidence);
        Assert.Contains("von keinem Block belegt", evidence);
        Assert.DoesNotContain("gelöscht", evidence);
        Assert.DoesNotContain("verschlüsselt", evidence);
    }

    [Fact]
    public void ScanAndChainWalk_AgreeOnTheSampleImage()
    {
        var chain = BoschBlockChain.Read(TriCoreDump.SampleMed17Image(), Layout(0x800000));

        Assert.Equal(chain.Blocks.Select(b => b.FileStart).OrderBy(x => x),
                     chain.ChainBlocks.Select(b => b.FileStart).OrderBy(x => x));

        Assert.Contains(chain.Evidence,
                        e => e.Contains("Abtastung und Kettenlauf liefern dieselben"));
    }

    // ==================================================================
    // Die Regeln, die einen Kopf bestätigen
    // ==================================================================

    [Fact]
    public void BlockEnd_MustBeStartPlusSizeMinusFour()
    {
        var block = TriCoreDump.Block(0x10, 0x80018000, 0x2000, 0, "10SW008917");

        // blockEnd = blockStart + size statt blockStart + size - 4.
        TestDump.WriteLe(block, BoschBlockChain.BlockEndOffset, 0x80018000 + 0x2000);

        var image = SingleBlockImage(block);
        Assert.False(BoschBlockChain.TryReadHeader(image, Layout(Pmu0Size), EntryFile, out var parsed));
        Assert.Null(parsed);
        Assert.Empty(BoschBlockChain.Read(image, Layout(Pmu0Size)).Blocks);
    }

    [Fact]
    public void MissingDeadbeefMarker_RejectsBlock()
    {
        var block = TriCoreDump.Block(0x10, 0x80018000, 0x2000, 0, "10SW008917");

        // Ein sonst tadelloser Kopf — nur der Magiewert am Blockende fehlt.
        TestDump.WriteLe(block, block.Length - 4, 0x12345678);

        var image = SingleBlockImage(block);
        Assert.False(BoschBlockChain.TryReadHeader(image, Layout(Pmu0Size), EntryFile, out _));
        Assert.Empty(BoschBlockChain.Read(image, Layout(Pmu0Size)).Blocks);
    }

    [Fact]
    public void TablePointerOutsideOwnBlock_RejectsHeader()
    {
        var block = TriCoreDump.Block(0x10, 0x80018000, 0x2000, 0, "10SW008917",
                                      table2: [0xDEAD0001, 0xDEAD0002]);

        // Zeiger auf eine Adresse, die zwar im Abbild liegt, aber nicht im Block.
        TestDump.WriteLe(block, BoschBlockChain.Table2PointerOffset, 0x80100000);

        var image = SingleBlockImage(block);
        Assert.False(BoschBlockChain.TryReadHeader(image, Layout(Pmu0Size), EntryFile, out _));
    }

    [Fact]
    public void BlockWithoutTables_CarriesZeroPointerAndZeroCount()
    {
        var block = TriCoreDump.Block(0x10, 0x80018000, 0x2000, 0, "10SW008917");
        var image = SingleBlockImage(block);

        Assert.True(BoschBlockChain.TryReadHeader(image, Layout(Pmu0Size), EntryFile, out var parsed));
        Assert.Empty(parsed!.Table1);
        Assert.Empty(parsed.Table2);
        Assert.Equal(0, parsed.Table1Cpu);
    }

    [Fact]
    public void ChecksumStructures_AreThirtyTwoBytesFromOffset34()
    {
        // Der Abgleich beider Quellen: 32 Byte je Struktur ab +0x34, danach ein
        // Prüfwort. Bei 1 Struktur landet die Tabelle also auf +0x58, bei 4 auf +0xB8.
        var one = TriCoreDump.Block(0x10, 0x80018000, 0x2000, 0, "10SW008917",
                                    table2: [0x11111111]);
        var four = TriCoreDump.Block(0x40, 0x80018000, 0x40000, 0, "10SW026798",
                                     table2: [0x22222222],
                                     algorithms: [0x00, 0x00, 0x00, 0x00]);

        Assert.Equal(0x54, BoschBlockChain.HeaderSize + 1 * BoschBlockChain.ChecksumStructureSize);
        Assert.Equal(0xB4, BoschBlockChain.HeaderSize + 4 * BoschBlockChain.ChecksumStructureSize);

        Assert.Equal(0x80018058u, TestDump.ReadLe(one, BoschBlockChain.Table2PointerOffset));
        Assert.Equal(0x800180B8u, TestDump.ReadLe(four, BoschBlockChain.Table2PointerOffset));
    }

    [Fact]
    public void TableEntriesAreNotValidatedAsAddresses()
    {
        // Die Einträge mischen Flash-, Extern- und LDRAM-Adressen mit
        // Wächterwerten und einer schlichten Zahl. Sie werden roh übernommen.
        uint[] mixed = [0x80141F04, 0x808084AC, 0xC0000070, 0x00000000, 0xFFFFFFFF, 0x00000F7C];

        var block = TriCoreDump.Block(0x40, 0x80018000, 0x2000, 0, "10SW026798", table2: mixed);
        var image = SingleBlockImage(block);

        Assert.True(BoschBlockChain.TryReadHeader(image, Layout(Pmu0Size), EntryFile, out var parsed));
        Assert.Equal(mixed, parsed!.Table2);
    }

    [Fact]
    public void TablePointerIntoOtherBank_ResolvesGlobally()
    {
        // Das Vorbild löste Zeiger gegen die Bank des Blocks auf und griff
        // deshalb bei 0x808084AC ins Leere. Hier wird global aufgelöst.
        var layout = Layout(0x800000);

        Assert.Equal(0x2084AC, layout.ToFile(0x808084AC));
        Assert.Equal("PMU1", layout.PartitionAt(0x2084AC)!.Label);

        // Und die Kette folgt demselben Weg: ASW #0 liegt in PMU0 und zeigt
        // auf ASW #1 in PMU1.
        var chain = BoschBlockChain.Read(TriCoreDump.SampleMed17Image(), layout);
        var asw0 = Assert.Single(chain.Blocks, b => b.Id == 0x40);

        Assert.Equal(0x80800000, asw0.NextCpu);
        Assert.Contains(chain.Blocks, b => b.CpuStart == 0x80800000);
    }

    // ==================================================================
    // Kettenlauf
    // ==================================================================

    [Fact]
    public void NextSectorZero_EndsChainCleanly()
    {
        var image = SingleBlockImage(TriCoreDump.Block(0x10, 0x80018000, 0x2000, 0, "10SW008917"));
        var chain = BoschBlockChain.Read(image, Layout(Pmu0Size));

        var block = Assert.Single(chain.ChainBlocks);
        Assert.Null(block.NextCpu);
        Assert.DoesNotContain(chain.Evidence, e => e.Contains("bricht ab"));
        Assert.DoesNotContain(chain.Evidence, e => e.Contains("im Kreis"));
    }

    [Fact]
    public void BrokenChainLink_StopsWalkWithoutClaimingBlocks()
    {
        // Der Einstiegsblock zeigt auf eine Adresse, an der kein Kopf steht.
        var image = TriCoreDump.Pflash(Pmu0Size,
            (EntryFile, TriCoreDump.Block(0x10, 0x80018000, 0x2000, 0x80100000, "10SW008917")));

        var chain = BoschBlockChain.Read(image, Layout(Pmu0Size));

        Assert.Single(chain.ChainBlocks);
        Assert.Contains(chain.Evidence, e => e.Contains("Kette bricht ab"));

        // Kein erfundener zweiter Block.
        Assert.Single(chain.Blocks);
    }

    [Fact]
    public void ChainBroken_ScanStillFindsBlocks()
    {
        // Zwei gültige Blöcke, aber die Kette führt nicht zum zweiten.
        var image = TriCoreDump.Pflash(Pmu0Size,
            (EntryFile, TriCoreDump.Block(0x10, 0x80018000, 0x2000, 0x80100000, "10SW008917")),
            (0x010000, TriCoreDump.Block(0x40, 0x80010000, 0x2000, 0, "10SW026798")));

        var chain = BoschBlockChain.Read(image, Layout(Pmu0Size));

        Assert.Equal(2, chain.Blocks.Count);          // Abtastung findet beide
        Assert.Single(chain.ChainBlocks);             // die Kette nur einen

        // Die Abweichung wird gemeldet, nicht stillschweigend vereinigt.
        Assert.Contains(chain.Evidence,
                        e => e.Contains("nur von der Abtastung gefunden"));
    }

    [Fact]
    public void CyclicChain_TerminatesAndIsReported()
    {
        var image = TriCoreDump.Pflash(Pmu0Size,
            (EntryFile, TriCoreDump.Block(0x10, 0x80018000, 0x2000, 0x80010000, "10SW008917")),
            (0x010000, TriCoreDump.Block(0x40, 0x80010000, 0x2000, 0x80018000, "10SW026798")));

        var chain = BoschBlockChain.Read(image, Layout(Pmu0Size));

        Assert.Equal(2, chain.ChainBlocks.Count);
        Assert.Contains(chain.Evidence, e => e.Contains("im Kreis"));
    }

    // ==================================================================
    // Prüfsummen
    // ==================================================================

    [Fact]
    public void TamperedCalibration_YieldsCrcMismatch_NotVerified()
    {
        var image = TriCoreDump.SampleMed17Image();

        // Ein Byte tief im Dataset-Block umkippen — mitten im geprüften Bereich.
        image[0x402000 + 0x1000] ^= 0xFF;

        var chain = BoschBlockChain.Read(image, Layout(0x800000));
        var dataset = Assert.Single(chain.Blocks, b => b.Id == 0x60);

        Assert.True(dataset.ChecksumMismatch);

        var sector = Assert.Single(BoschBlockChain.ToSectors(chain.Blocks),
                                   s => s.Kind == SectorKind.Dataset);
        Assert.Equal(SectorStatus.CrcMismatch, sector.Status);
        Assert.NotEqual(SectorStatus.Verified, sector.Status);

        // Alle übrigen Blöcke bleiben unberührt bestätigt.
        Assert.All(chain.Blocks.Where(b => b.Id != 0x60), b => Assert.False(b.ChecksumMismatch));
    }

    [Fact]
    public void AllThreeAlgorithms_AreComputedInTheSampleImage()
    {
        var chain = BoschBlockChain.Read(TriCoreDump.SampleMed17Image(), Layout(0x800000));
        var asw0 = Assert.Single(chain.Blocks, b => b.Id == 0x40);

        Assert.Equal(4, asw0.Checksums.Count);
        Assert.Equal([0x00, 0x01, 0x10, 0x00], asw0.Checksums.Select(c => c.Algorithm));
        Assert.All(asw0.Checksums, c => Assert.True(c.Ok));
    }

    [Fact]
    public void UnknownAlgorithmInStructure_IsReportedNotGuessed()
    {
        var block = TriCoreDump.Block(0x10, 0x80018000, 0x2000, 0, "10SW008917");

        // Kennung 0x02 gibt es nicht.
        TestDump.WriteLe(block, BoschBlockChain.HeaderSize + 0x1C, 0x02);

        var image = SingleBlockImage(block);
        var chain = BoschBlockChain.Read(image, Layout(Pmu0Size));

        var structure = Assert.Single(Assert.Single(chain.Blocks).Checksums);
        Assert.Null(structure.Ok);
        Assert.Null(structure.Computed);
        Assert.Contains("nicht bekannt", structure.Note);
    }

    // ==================================================================
    // Kein gestelltes Stellwort ist keine Abweichung
    // ==================================================================

    private const long UnstampedCpu = 0x80018000;
    private const long UnstampedSize = 0x2000;

    /// <summary>
    /// Baut einen Block, dessen einzige Prüfsummenstruktur den <em>eigenen Kopf</em>
    /// einschließt — und damit das Stellwort bei +0x30.
    ///
    /// Der Baukasten legt den geprüften Bereich sonst hinter den Kopf, und dort
    /// hätte das Stellwort keine Wirkung. In echten Abbildern liegt es anders:
    /// über die 8 441 Blöcke des VAG-Bestands schließt genau <em>eine</em>
    /// Struktur je Block den eigenen Kopf ein — das ist die, deren Rechnung am
    /// Stellwort hängt.
    ///
    /// Der Bereich wächst dabei über den, für den gestellt wurde; die Prüfsumme
    /// geht also nicht mehr auf. Genau das ist der Ausgangspunkt beider Fälle —
    /// unterschieden werden sie allein durch das Stellwort.
    /// </summary>
    private static byte[] BlockCheckedFromItsOwnHeader(uint adjustWord)
    {
        var block = TriCoreDump.Block(0x10, UnstampedCpu, UnstampedSize, 0, "10SW008917");

        TestDump.WriteLe(block, BoschBlockChain.HeaderSize + 0x04, (uint)UnstampedCpu);
        TestDump.WriteLe(block, BoschBlockChain.ChecksumAdjustOffset, adjustWord);

        return block;
    }

    [Fact]
    public void UnstampedAdjustWord_IsNotReportedAsMismatch()
    {
        // 0xAF ist in dieser Gerätefamilie das Füllbyte für „nicht gesetzt".
        var image = SingleBlockImage(BlockCheckedFromItsOwnHeader(BoschBlockChain.AdjustFillAf));
        var chain = BoschBlockChain.Read(image, Layout(Pmu0Size));

        var block = Assert.Single(chain.Blocks);
        var structure = Assert.Single(block.Checksums);

        // Die Rechnung geht nicht auf — sie kann es gar nicht.
        Assert.False(structure.Ok);
        Assert.True(structure.NotStamped);

        Assert.True(block.ChecksumNotStamped);
        Assert.False(block.ChecksumMismatch);    // und das ist der ganze Punkt
        Assert.False(block.ChecksumsVerified);   // „in Ordnung" ist es deshalb nicht

        Assert.Equal(0, chain.VerifiedCount);

        // Der Befund nennt den Grund und die gerechnete Zahl — als Angabe,
        // nicht als Vorwurf.
        Assert.Contains("nicht gestellt", structure.ResultText);
        Assert.Contains("0xAFAFAFAF", structure.ResultText);
        Assert.DoesNotContain("weicht ab", structure.ResultText);

        var sector = Assert.Single(BoschBlockChain.ToSectors(chain.Blocks));
        Assert.Equal(SectorStatus.ChecksumNotStamped, sector.Status);
        Assert.NotEqual(SectorStatus.CrcMismatch, sector.Status);
        Assert.False(sector.CrcOk);
        Assert.True(sector.Present);
    }

    [Fact]
    public void StampedAdjustWord_StillYieldsMismatch()
    {
        // Dieselbe nicht aufgehende Rechnung, nur mit gestelltem Stellwort. Das
        // ist die Gegenprobe zum Fall darüber: die echten Abweichungen sind der
        // eigentliche Wert des Werkzeugs und dürfen nicht mit verschwinden.
        var image = SingleBlockImage(BlockCheckedFromItsOwnHeader(0x12345678));
        var chain = BoschBlockChain.Read(image, Layout(Pmu0Size));

        var block = Assert.Single(chain.Blocks);
        var structure = Assert.Single(block.Checksums);

        Assert.False(structure.Ok);
        Assert.False(structure.NotStamped);

        Assert.True(block.ChecksumMismatch);
        Assert.False(block.ChecksumNotStamped);
        Assert.Contains("weicht ab", structure.ResultText);

        var sector = Assert.Single(BoschBlockChain.ToSectors(chain.Blocks));
        Assert.Equal(SectorStatus.CrcMismatch, sector.Status);
    }

    [Fact]
    public void UnstampedAdjustWord_OutsideTheCheckedRange_StaysAMismatch()
    {
        // Das Stellwort erklärt nur die Bereiche, die es einschließen. Liegt es
        // daneben, ist eine nicht aufgehende Prüfsumme wieder das, wonach sie
        // aussieht — sonst entschuldigte ein einziges Füllwort im Kopf jede
        // Abweichung im ganzen Block.
        var block = TriCoreDump.Block(0x10, UnstampedCpu, UnstampedSize, 0, "10SW008917");
        TestDump.WriteLe(block, BoschBlockChain.ChecksumAdjustOffset, BoschBlockChain.AdjustFillAf);
        block[UnstampedSize - 0x100] ^= 0xFF;    // ein Byte im geprüften Bereich kippen

        var chain = BoschBlockChain.Read(SingleBlockImage(block), Layout(Pmu0Size));

        var read = Assert.Single(chain.Blocks);
        var structure = Assert.Single(read.Checksums);

        Assert.False(structure.Ok);
        Assert.False(structure.NotStamped);
        Assert.True(read.ChecksumMismatch);
    }

    // ==================================================================
    // CVN
    // ==================================================================

    [Fact]
    public void CvnConfigNotFound_IsReportedAsNotFound()
    {
        // Kein Ratewert, keine Null — gar keine CVN.
        var chain = BoschBlockChain.Read(TriCoreDump.SampleMed17Image(), Layout(0x800000));
        Assert.Null(chain.Cvn);
    }

    [Fact]
    public void CvnConfig_IsReadAndComputed()
    {
        const long DatasetCpu = 0x80010000;
        const long DatasetSize = 0x4000;

        var dataset = TriCoreDump.Block(0x60, DatasetCpu, DatasetSize, 0, "10SW028601",
                                        variant: "34/1/EDC17_C46/5/P643//C643X5L8///");

        // Konfigurationsstruktur {Zeiger, DS_START, DS_WOCS_END, Anzahl} und
        // dahinter die Tabelle aus (Start, Ende)-Paaren.
        const long ConfigAt = 0x1000;
        const long TableAt = 0x1010;

        TestDump.WriteLe(dataset, ConfigAt, (uint)(DatasetCpu + TableAt));
        TestDump.WriteLe(dataset, ConfigAt + 4, (uint)DatasetCpu);
        TestDump.WriteLe(dataset, ConfigAt + 8, (uint)(DatasetCpu + DatasetSize - 4));
        TestDump.WriteLe(dataset, ConfigAt + 12, 1);

        TestDump.WriteLe(dataset, TableAt, (uint)(DatasetCpu + 0x2000));
        TestDump.WriteLe(dataset, TableAt + 4, (uint)(DatasetCpu + 0x2FFC));

        var image = TriCoreDump.Pflash(Pmu0Size, (0x010000, dataset));
        var chain = BoschBlockChain.Read(image, Layout(Pmu0Size));

        Assert.NotNull(chain.Cvn);
        var range = Assert.Single(chain.Cvn!.Ranges);
        Assert.Equal(DatasetCpu + 0x2000, range.Start);

        uint expected = BoschChecksum.CvnFinish(
            BoschChecksum.CvnUpdate(BoschChecksum.CvnStart,
                                    image.AsSpan(0x010000 + 0x2000, 0x1000)));
        Assert.Equal(expected, chain.Cvn.Value);
    }

    // ==================================================================
    // Variantenkennung
    // ==================================================================

    [Fact]
    public void EcuVariantString_IsReadFromDatasetBlock()
    {
        // Feste Fundstelle: Dataset-Block, +0x78, schrägstrichgetrennt.
        var dataset = TriCoreDump.Block(0x60, 0x80018000, 0x4000, 0, "10SW028601",
                                        variant: "34/1/EDC17_C46/5/P643//C643X5L8///");

        var image = SingleBlockImage(dataset);
        var chain = BoschBlockChain.Read(image, Layout(Pmu0Size));

        Assert.Equal("34/1/EDC17_C46/5/P643//C643X5L8///", chain.Variant);
        Assert.Equal(EntryFile + BoschBlockChain.VariantOffset, chain.VariantOffset);

        var identity = BoschIdentity.Scan(image, chain.Variant, chain.VariantOffset ?? 0);

        var hit = Assert.Single(identity!.Hits, h => h.Kind == "Steuergerät");
        Assert.Equal("EDC17_C46", hit.Value);
        Assert.Equal(RegionConfidence.Confirmed, hit.Confidence);
    }

    [Fact]
    public void SampleImage_NamesItsOwnVariant()
    {
        var chain = BoschBlockChain.Read(TriCoreDump.SampleMed17Image(), Layout(0x800000));

        Assert.Equal("34/1/MED17.1.6/5/P643//C643X5L8///", chain.Variant);
        Assert.Equal(0x402000 + BoschBlockChain.VariantOffset, chain.VariantOffset);
    }

    /// <summary>
    /// Die beiden Endfelder des Blockkopfs folgen <em>verschiedenen</em>
    /// Konventionen: <c>blockEnd</c> zeigt auf das letzte Wort — den
    /// <c>0xDEADBEEF</c>-Abschluss —, <c>csEnd</c> dagegen auf das letzte Byte
    /// des geprüften Bereichs. Der Analogieschluss vom einen aufs andere ist
    /// naheliegend und falsch; er stand einmal im Leser und ließ 48 von 59
    /// Prüfsummen echter EDC17-Abbilder scheitern.
    /// </summary>
    [Fact]
    public void ChecksumEnd_PointsAtTheLastByte_WhileBlockEndPointsAtTheLastWord()
    {
        var image = TriCoreDump.Pflash(0x200000,
            (EntryFile, TriCoreDump.Block(0x10, 0x80018000, 0x2000, 0, "10SW008917")));
        var layout = Layout(0x200000);

        Assert.True(BoschBlockChain.TryReadHeader(image, layout, EntryFile, out var block));

        var check = Assert.Single(BoschBlockChain.VerifyChecksums(image, layout, block!));
        Assert.True(check.Ok);

        Assert.Equal(0, block!.CpuEnd % 4);        // blockEnd: wortbündig
        Assert.NotEqual(0, check.CsEnd % 4);       // csEnd: gerade nicht
    }
}
