using System.Text;

namespace VolvoSplitter.Core.TriCore;

/// <summary>Eine Prüfsummenstruktur eines Blocks, 32 Byte, samt Ergebnis der Nachrechnung.</summary>
/// <param name="Computed">Null, wenn der Bereich nicht auflösbar oder der Algorithmus unbekannt ist.</param>
/// <param name="Ok">Null heißt „nicht nachgerechnet", nicht „in Ordnung".</param>
public sealed record BoschChecksumStructure(
    byte BlockId, long CsStart, long CsEnd, uint StartValue, uint ExpectedValue,
    uint BlockIdRef, uint BlockIdAddr, byte Algorithm,
    uint? Computed, bool? Ok, string Note)
{
    public string AlgorithmName => BoschChecksum.Name(Algorithm);

    public string RangeText => $"{Hex.Addr(CsStart)} – {Hex.Addr(CsEnd)}";

    public string ResultText => Ok switch
    {
        true => $"stimmt (0x{Computed:X8})",
        false => Computed is { } value
            ? $"weicht ab: gerechnet 0x{value:X8}, erwartet " +
              $"0x{BoschChecksum.Expected(Algorithm, ExpectedValue):X8}"
            : "weicht ab",
        _ => Note
    };
}

/// <summary>Die CVN samt der Bereiche, über die sie läuft.</summary>
public sealed record BoschCvn(uint Value, IReadOnlyList<(long Start, long End)> Ranges, long ConfigOffset)
{
    public string ValueText => $"0x{Value:X8}";
}

/// <summary>
/// Ein Block der Bosch-Blockkette. Die Felder stehen so im Abbild; gedeutet
/// wird nur der Name der Blockart (<see cref="IdName"/>) — und der stammt aus
/// den ausgewerteten Werkzeugen, nicht aus einer Bosch-Unterlage.
/// </summary>
public sealed record BoschBlock(
    long CpuStart, long FileStart, long Size, long CpuEnd,
    byte Id, string IdName, uint Flags,
    long? NextCpu,
    long Table1Cpu, IReadOnlyList<uint> Table1,
    long Table2Cpu, IReadOnlyList<uint> Table2,
    string Identifier, int ChecksumStructureCount)
{
    public long FileEnd => FileStart + Size;

    /// <summary>One-Time-Programmable: Bit 0x00800000 im Kennungswort.</summary>
    public bool Otp => (Flags & BoschBlockChain.OtpFlag) != 0;

    public IReadOnlyList<BoschChecksumStructure> Checksums { get; init; } = [];

    /// <summary>Die acht unerklärten Bytes bei +0x24 — roh, ungedeutet.</summary>
    public byte[] UnknownBytes { get; init; } = [];

    /// <summary>Abgleichwort bei +0x30.</summary>
    public uint ChecksumAdjust { get; init; }

    /// <summary>Steuergerätevariante aus dem Dataset-Block bei +0x78, falls vorhanden.</summary>
    public string? Variant { get; init; }

    /// <summary>Stellung in der Kette, falls der Kettenlauf den Block erreicht hat.</summary>
    public int? ChainOrder { get; init; }

    public bool ChecksumsComputed => Checksums.Any(c => c.Ok is not null);
    public bool ChecksumMismatch => Checksums.Any(c => c.Ok == false);

    public string AddressRange => Hex.Range(CpuStart, CpuEnd + 4);
    public string FileRange => Hex.Range(FileStart, FileEnd);
    public string SizeText => $"{Size:N0} B";
}

/// <summary>
/// Ergebnis eines Laufs über die Blockstruktur eines TriCore-Abbilds.
/// </summary>
/// <param name="Blocks">Bestätigte Blöcke in Datei-Reihenfolge (Ergebnis der Abtastung).</param>
/// <param name="ChainBlocks">Blöcke, die der Kettenlauf über <c>nextSector</c> erreicht hat.</param>
/// <param name="Gaps">Von keinem Block belegte Bereiche — Beobachtung, ohne Deutung.</param>
public sealed record BoschChainResult(
    IReadOnlyList<BoschBlock> Blocks,
    IReadOnlyList<BoschBlock> ChainBlocks,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<(long From, long To)> Gaps,
    BoschCvn? Cvn,
    string? Variant,
    long? VariantOffset)
{
    public static readonly BoschChainResult Empty =
        new([], [], [], [], null, null, null);

    public int VerifiedCount => Blocks.Count(b => !b.ChecksumMismatch);
}

/// <summary>
/// Liest die verkettete Blockstruktur eines Bosch-EDC17-/MED17-Abbilds.
///
///     +0x00  u32   blockIdentifier   unteres Byte = Blockart, Bit 0x00800000 = OTP
///     +0x04  u32   size
///     +0x08  u32   nextSector        CPU-Adresse des nächsten Kopfes, 0 = Kettenende
///     +0x0C  u32   blockEnd          = blockStart + size - 4
///     +0x10  u32   table1Pointer
///     +0x14  u32   table2Pointer
///     +0x18  u8    table1Size
///     +0x19  u8    table2Size
///     +0x1A  10 B  swIdentifier      ASCII, z. B. „10SW008917"
///     +0x24  8 B   unerklärt         im Beispiel 0xFF-Füllung
///     +0x2C  u32   numChecksumStructures
///     +0x30  u32   checksumAdjust
///     +0x34  n×32  Prüfsummenstrukturen
///     Ende-4 u32   0xDEADBEEF
///
/// Alles little endian.
///
/// <strong>Herkunft.</strong> Die Struktur stammt aus zwei unabhängig
/// entstandenen Community-Werkzeugen — <c>github.com/fanyi3315/bosch-med17-block-reader</c>
/// und einem „MEDC17 Checksum Analyzer" —, die im Blockkopf exakt
/// übereinstimmen. Beide sind Reverse Engineering an <em>einem</em> Abbild,
/// kein Herstellerdokument. Die Namen der Blockarten und der Algorithmen sind
/// Deutung. Deshalb setzt dieser Leser die Struktur nicht voraus, sondern
/// prüft sie an jedem einzelnen Kopf nach — und rechnet die Prüfsummen nach.
///
/// <strong>Zwei Fallen des Vorbilds werden nicht übernommen:</strong>
/// Zeiger werden über <see cref="PhysicalLayout.ToFile"/> global aufgelöst und
/// nicht relativ zur Bank des Blocks; und die Einträge der Zeigertabellen
/// werden roh ausgegeben, ohne als Adressen geprüft zu werden — sie enthalten
/// nachweislich Flash-, Extern- und LDRAM-Adressen, Wächterwerte und schlichte
/// Zahlen nebeneinander.
/// </summary>
public static class BoschBlockChain
{
    public const int HeaderSize = 0x34;
    public const int ChecksumStructureSize = 0x20;

    public const uint EndMarker = 0xDEADBEEF;
    public const uint OtpFlag = 0x00800000;

    public const int SizeOffset = 0x04;
    public const int NextSectorOffset = 0x08;
    public const int BlockEndOffset = 0x0C;
    public const int Table1PointerOffset = 0x10;
    public const int Table2PointerOffset = 0x14;
    public const int Table1SizeOffset = 0x18;
    public const int Table2SizeOffset = 0x19;
    public const int SwIdentifierOffset = 0x1A;
    public const int SwIdentifierLength = 10;
    public const int UnknownOffset = 0x24;
    public const int UnknownLength = 8;
    public const int ChecksumCountOffset = 0x2C;
    public const int ChecksumAdjustOffset = 0x30;

    /// <summary>Schrägstrichgetrennte Variantenkennung im Dataset-Block.</summary>
    public const int VariantOffset = 0x78;

    /// <summary>Deckel des Vorbilds — mehr Strukturen gelten als unplausibel.</summary>
    public const int MaxChecksumStructures = 100;

    public const int MaxChainSteps = 64;
    public const long MinBlockSize = 0x40;

    /// <summary>Einstiegspunkt der Kette im ausgewerteten Abbild.</summary>
    public const long EntryPoint = 0x80018000;

    /// <summary>
    /// Blockarten. Die Bedeutungen stammen aus den ausgewerteten Werkzeugen und
    /// sind eine Deutung, keine Bosch-Angabe.
    /// </summary>
    public static readonly IReadOnlyDictionary<byte, string> BlockKinds =
        new Dictionary<byte, string>
        {
            [0x10] = "Startup Block",
            [0x20] = "Tuning protection",
            [0x30] = "Customer Block",
            [0x40] = "Application software #0",
            [0x50] = "Application software #1",
            [0x60] = "Dataset #0",
            [0x70] = "Dataset #1",
            [0x80] = "Variant dataset",
            [0x90] = "Customer Tuning protection",
            [0xA0] = "Application software #2",
            [0xB0] = "Application software #3",
            [0xC0] = "Absolute constants #0",
            [0xD0] = "Emulation extension chip",
            [0xE0] = "Customer specific",
            [0xF0] = "Ramloader",
            [0xF1] = "Application Attestation"
        };

    /// <summary>Kennung des Dataset-Blocks, in dem die Variantenzeichenkette steht.</summary>
    public const byte DatasetId = 0x60;

    // ==================================================================
    // Einstiegspunkt
    // ==================================================================

    /// <summary>
    /// Liest die Blockstruktur mit beiden Verfahren: Abtastung als Hauptweg,
    /// Kettenlauf als Gegenprobe. Stimmen sie überein, ist das ein eigener
    /// Beleg; weichen sie ab, wird die Differenz gemeldet — nicht stillschweigend
    /// vereinigt.
    /// </summary>
    public static BoschChainResult Read(byte[] data, PhysicalLayout layout, bool verifyChecksums = true)
    {
        var evidence = new List<string>();
        var scanned = ScanHeaders(data, layout, evidence);

        if (scanned.Count == 0)
        {
            evidence.Add("Kein bestätigter Bosch-Blockkopf gefunden");
            return BoschChainResult.Empty with { Evidence = evidence };
        }

        var chain = WalkChain(data, layout, scanned, evidence);

        if (verifyChecksums)
            for (int i = 0; i < scanned.Count; i++)
                scanned[i] = scanned[i] with
                {
                    Checksums = VerifyChecksums(data, layout, scanned[i])
                };

        CompareMethods(scanned, chain, evidence);

        var blocks = ApplyChainOrder(scanned, chain);
        var dataset = blocks.FirstOrDefault(b => b.Id == DatasetId);

        string? variant = dataset?.Variant;
        long? variantOffset = variant is null ? null : dataset!.FileStart + VariantOffset;

        var gaps = Gaps(blocks, data.LongLength);
        if (gaps.Count > 0)
            evidence.Add($"{gaps.Count} Bereich(e) von keinem Block belegt — als Beobachtung " +
                         "aufgeführt, ohne Deutung");

        var cvn = dataset is null ? null : FindCvn(data, layout, dataset);

        return new BoschChainResult(blocks, chain, evidence, gaps, cvn, variant, variantOffset);
    }

    // ==================================================================
    // Abtastung — das Hauptverfahren
    // ==================================================================

    /// <summary>
    /// Sucht Blockköpfe im ganzen Abbild, unabhängig von der Verkettung. Findet
    /// damit auch Blöcke, deren Kette gerissen ist oder deren Kopf nicht am
    /// erwarteten Einstiegspunkt liegt. Die Prüfsummen werden hier noch nicht
    /// gerechnet — dieser Durchlauf muss billig bleiben.
    /// </summary>
    public static List<BoschBlock> ScanHeaders(byte[] data, PhysicalLayout layout,
                                               List<string>? evidence = null)
    {
        var found = new List<BoschBlock>();
        var rejected = new List<long>();
        long at = 0;

        while (at + HeaderSize <= data.LongLength)
        {
            // Billige Vorprüfung: unteres Byte muss eine bekannte Blockart sein,
            // das oberste Byte des Kennungsworts ist in allen ausgewerteten
            // Ständen null. Alles Weitere entscheidet die volle Prüfung.
            if (!BlockKinds.ContainsKey(data[at]) || data[at + 3] != 0)
            {
                at += 4;
                continue;
            }

            if (TryReadHeader(data, layout, at, out var block) && block is not null)
            {
                found.Add(block);

                // Hinter den Block springen — aufgerundet, damit das 4-Byte-Raster
                // nicht in den eben gelesenen Block zurückfällt.
                at = block.FileEnd + 3;
                at -= at % 4;
                continue;
            }

            // Ein Kandidat, der der Vorprüfung genügt, aber nicht der vollen
            // Prüfung. Wird gemeldet, aber nicht als Block ausgegeben.
            if (LooksLikeCandidate(data, at)) rejected.Add(at);
            at += 4;
        }

        ReportRejected(rejected, evidence);
        return found;
    }

    /// <summary>
    /// Zweite, immer noch billige Stufe: nur Kandidaten, deren Größenfeld
    /// überhaupt in die Datei passt, sind eine Meldung wert. Sonst wäre die
    /// Belegliste voller Zufallstreffer.
    /// </summary>
    private static bool LooksLikeCandidate(byte[] data, long at)
    {
        long size = ByteOrder.ReadUInt32(data, at + SizeOffset, Endianness.Little);
        return size >= MinBlockSize && at + size <= data.LongLength;
    }

    private static void ReportRejected(List<long> rejected, List<string>? evidence)
    {
        if (evidence is null || rejected.Count == 0) return;

        const int Show = 5;
        foreach (long at in rejected.Take(Show))
            evidence.Add($"Blockkopfkandidat bei {Hex.Addr(at)} nicht bestätigt");

        if (rejected.Count > Show)
            evidence.Add($"… und {rejected.Count - Show} weitere nicht bestätigte Kandidaten");
    }

    // ==================================================================
    // Den Nullpunkt messen, statt ihn zu setzen
    // ==================================================================

    /// <summary>
    /// Höchstens so viele gemessene Nullpunkte werden weiterverfolgt. Im Bestand
    /// von 1516 Abbildern nennt kein einziges zwei widersprüchliche Basen; der
    /// Deckel begrenzt also nicht die Aussage, sondern nur die Arbeit — jeder
    /// Kandidat kostet einen weiteren vollen Abtastdurchlauf.
    /// </summary>
    public const int MaxWindowStarts = 4;

    /// <summary>
    /// Normalisiert liegen Programm-, Extern- und Datenflash eines TriCore alle
    /// in <c>0x80000000…0x8FFFFFFF</c> (der Spiegel <c>0xA…</c> ist ausmaskiert).
    /// Ein Nullpunkt außerhalb wäre keine Flashadresse.
    /// </summary>
    private const long FlashSpan = 0x10000000;

    /// <summary>
    /// Misst, welche CPU-Adresse zum Datei-Offset 0 gehört — statt sie zu setzen.
    ///
    /// Jeder Blockkopf nennt seine Lage selbst: <c>blockEnd</c> zeigt auf das
    /// letzte Wort, also ist <c>blockStart = blockEnd − size + 4</c> und der
    /// Nullpunkt <c>blockStart − fileStart</c>. Gesucht wird deshalb mit genau
    /// den Regeln von <see cref="TryReadHeader"/>, die <em>ohne</em> Layout
    /// auskommen: bekannte Blockart, Größe passt in die Datei, <c>0xDEADBEEF</c>
    /// am errechneten Blockende, plausible Strukturzahl. Die Regeln, die eine
    /// Basis voraussetzen, bleiben draußen — sie ist ja das Gesuchte.
    ///
    /// <strong>Das Ergebnis ist ein Vorschlag, kein Befund.</strong> Ein einzelner
    /// Kopf bestätigt den aus ihm selbst abgeleiteten Nullpunkt zwangsläufig;
    /// eine Mehrheit unter den Rohkandidaten wäre deshalb ein schwacher Beleg.
    /// Entschieden wird stattdessen mit den <em>vollen</em> Regeln, in
    /// <see cref="EcuDetector"/>: eine gemessene Basis gilt erst, wenn sie dort
    /// <em>mehr</em> Köpfe bestätigt als jeder Vorgabekandidat — Gleichstand
    /// bleibt bei der Vorgabe. Damit hängt die Basis an derselben Messung, die
    /// schon über die Bankaufteilung entscheidet, und nicht an einer zweiten,
    /// schwächeren Regel daneben.
    ///
    /// Zurück kommen die Kandidaten nach Stimmenzahl geordnet, die häufigste
    /// zuerst; die Reihenfolge steuert nur, welche bei <see cref="MaxWindowStarts"/>
    /// überhaupt geprüft werden.
    /// </summary>
    public static List<long> MeasureWindowStarts(byte[] data)
    {
        var votes = new Dictionary<long, int>();

        for (long at = 0; at + HeaderSize <= data.LongLength; at += 4)
        {
            if (!BlockKinds.ContainsKey(data[at]) || data[at + 3] != 0) continue;

            long size = ByteOrder.ReadUInt32(data, at + SizeOffset, Endianness.Little);
            if (size < MinBlockSize || at + size > data.LongLength) continue;

            if (ByteOrder.ReadUInt32(data, at + size - 4, Endianness.Little) != EndMarker) continue;

            long structures = ByteOrder.ReadUInt32(data, at + ChecksumCountOffset, Endianness.Little);
            if (structures > MaxChecksumStructures) continue;
            if (HeaderSize + structures * ChecksumStructureSize + 4 > size) continue;

            long blockEnd = PhysicalLayout.Normalize(
                ByteOrder.ReadUInt32(data, at + BlockEndOffset, Endianness.Little));
            long start = blockEnd - size + 4 - at;

            // blockEnd zeigt auf das 0xDEADBEEF-Wort und ist damit wortbündig;
            // ein krummer Nullpunkt kann nur aus Rauschen stammen.
            if (start % 4 != 0) continue;
            if (start < TriCoreDevice.PflashBase ||
                start >= TriCoreDevice.PflashBase + FlashSpan) continue;

            votes[start] = votes.GetValueOrDefault(start) + 1;
        }

        return votes.OrderByDescending(v => v.Value)
                    .ThenBy(v => v.Key)
                    .Take(MaxWindowStarts)
                    .Select(v => v.Key)
                    .ToList();
    }

    /// <summary>
    /// Prüft einen Blockkopf. Erst wenn <em>alle</em> Regeln zutreffen, gilt er
    /// als gültig. Die Reihenfolge folgt der Schärfe: der Magiewert am Blockende
    /// ist unabhängig von jeder Adressrechnung und steht deshalb vorn.
    /// </summary>
    public static bool TryReadHeader(byte[] data, PhysicalLayout layout, long fileStart,
                                     out BoschBlock? block)
    {
        block = null;

        if (fileStart < 0 || fileStart + HeaderSize > data.LongLength) return false;

        // 2. Blockart bekannt
        byte id = data[fileStart];
        if (!BlockKinds.TryGetValue(id, out var idName)) return false;

        uint identifier = ByteOrder.ReadUInt32(data, fileStart, Endianness.Little);
        long size = ByteOrder.ReadUInt32(data, fileStart + SizeOffset, Endianness.Little);

        // 3. Größe plausibel, Block passt vollständig in die Datei.
        //    Keine Prüfung auf Sektorbündigkeit — Blockgrößen sind es nicht.
        if (size < MinBlockSize || fileStart + size > data.LongLength) return false;

        // 1. 0xDEADBEEF am Blockende — die schärfste Einzelprüfung.
        if (ByteOrder.ReadUInt32(data, fileStart + size - 4, Endianness.Little) != EndMarker)
            return false;

        // 5. Der Kopf liegt an einer Adresse, die das Layout kennt …
        if (layout.ToCpu(fileStart) is not { } cpuStart) return false;

        // … und der ganze Block liegt in derselben physischen Bank. Bänke sind
        //    im CPU-Raum nicht zusammenhängend; ein bankübergreifender Block
        //    wäre eine Fehldeutung.
        var partition = layout.PartitionAt(fileStart);
        if (partition is null || !partition.Contains(fileStart + size - 1)) return false;

        // 4. blockEnd == blockStart + size - 4, exakt und ohne Toleranz.
        long blockEnd = ByteOrder.ReadUInt32(data, fileStart + BlockEndOffset, Endianness.Little);
        if (PhysicalLayout.Normalize(blockEnd) != PhysicalLayout.Normalize(cpuStart + size - 4))
            return false;

        // 6. Anzahl der Prüfsummenstrukturen plausibel und im Block untergebracht.
        long structureCount = ByteOrder.ReadUInt32(data, fileStart + ChecksumCountOffset,
                                                   Endianness.Little);
        if (structureCount > MaxChecksumStructures) return false;
        if (HeaderSize + structureCount * ChecksumStructureSize + 4 > size) return false;

        // 7. Zeigertabellen liegen im eigenen Block.
        long table1Cpu = ByteOrder.ReadUInt32(data, fileStart + Table1PointerOffset, Endianness.Little);
        long table2Cpu = ByteOrder.ReadUInt32(data, fileStart + Table2PointerOffset, Endianness.Little);
        int table1Size = data[fileStart + Table1SizeOffset];
        int table2Size = data[fileStart + Table2SizeOffset];

        if (!TryReadTable(data, layout, fileStart, size, table1Cpu, table1Size, out var table1))
            return false;
        if (!TryReadTable(data, layout, fileStart, size, table2Cpu, table2Size, out var table2))
            return false;

        long next = ByteOrder.ReadUInt32(data, fileStart + NextSectorOffset, Endianness.Little);

        // Die Kennung wird gelesen, nicht geprüft: sie steht deshalb hier unten
        // und nicht mehr bei den Regeln. Begründung an ReadIdentifier.
        string swIdentifier = ReadIdentifier(data, fileStart + SwIdentifierOffset);

        block = new BoschBlock(
            cpuStart, fileStart, size, blockEnd,
            id, idName, identifier & 0xFFFFFF00u,
            next == 0 ? null : next,
            table1Cpu, table1, table2Cpu, table2,
            swIdentifier, (int)structureCount)
        {
            UnknownBytes = data.AsSpan((int)(fileStart + UnknownOffset), UnknownLength).ToArray(),
            ChecksumAdjust = ByteOrder.ReadUInt32(data, fileStart + ChecksumAdjustOffset,
                                                  Endianness.Little),
            Variant = id == DatasetId ? ReadVariant(data, fileStart, size) : null
        };
        return true;
    }

    /// <summary>
    /// Liest das Kennungsfeld als druckbares ASCII; <c>0x00</c> und <c>0xFF</c>
    /// gelten als Füllung und werden übersprungen. Lässt sich das Feld so nicht
    /// lesen, kommt die <em>leere</em> Kennung zurück.
    ///
    /// <strong>Und genau das verwirft den Kopf nicht mehr.</strong> Bis hierher
    /// war das eine strukturelle Prüfung: ein einziges Byte außerhalb des
    /// Textbereichs ließ <c>TryReadHeader</c> mit <c>false</c> zurückkehren,
    /// obwohl die vier scharfen Regeln — <c>0xDEADBEEF</c>, <c>blockEnd</c>,
    /// Größe und Strukturzahl — längst durch waren. Ein unlesbares Kennungsfeld
    /// ist aber eine fehlende Kennung, kein ungültiger Kopf.
    ///
    /// Gemessen an 1516 VAG-EDC17-Abbildern: 25 davon füllen das Feld mit
    /// <c>0xAF</c> statt mit <c>0x00</c> oder <c>0xFF</c> und verloren dadurch
    /// zusammen 123 Blöcke, 14 von ihnen restlos alle. <c>0xAF</c> als drittes
    /// Füllbyte zu <em>benennen</em> wäre dagegen nicht gedeckt: in keiner der
    /// ausgewerteten Unterlagen kommt der Wert als Bosch-Wächterwert vor — er
    /// steht nur in diesen Abbildern. Deshalb wird kein neues Füllbyte
    /// eingeführt, sondern die Folge des Nichtlesens berichtigt.
    ///
    /// Alles oder nichts: ein Feld mit einem einzigen Fremdbyte ergibt keine
    /// halbe Kennung. Aus Binärrauschen den druckbaren Teil herauszuklauben
    /// erfände eine Teilenummer, die es nicht gibt.
    /// </summary>
    private static string ReadIdentifier(byte[] data, long at)
    {
        var text = new StringBuilder();

        for (int i = 0; i < SwIdentifierLength; i++)
        {
            byte b = data[at + i];
            if (b is 0xFF or 0x00) continue;
            if (b < 0x20 || b > 0x7E) return "";
            text.Append((char)b);
        }

        return text.ToString();
    }

    /// <summary>
    /// Liest eine Zeigertabelle. Der Zeiger wird global über das Layout
    /// aufgelöst — nie relativ zur Bank des Blocks. Die Tabelle selbst muss
    /// aber im eigenen Block liegen; das ist die Regel, die den Kopf bestätigt.
    /// </summary>
    private static bool TryReadTable(byte[] data, PhysicalLayout layout, long blockStart, long blockSize,
                                     long tableCpu, int count, out IReadOnlyList<uint> table)
    {
        table = [];
        if (count == 0) return true;

        if (layout.ToFile(tableCpu) is not { } tableFile) return false;
        if (tableFile < blockStart || tableFile + count * 4L > blockStart + blockSize) return false;

        var entries = new uint[count];
        for (int i = 0; i < count; i++)
            entries[i] = ByteOrder.ReadUInt32(data, tableFile + i * 4L, Endianness.Little);

        // Die Einträge werden bewusst nicht geprüft: sie mischen Flash-,
        // Extern- und LDRAM-Adressen mit Wächterwerten und schlichten Zahlen.
        table = entries;
        return true;
    }

    /// <summary>
    /// Schrägstrichgetrennte Variantenkennung im Dataset-Block bei +0x78, etwa
    /// <c>34/1/EDC17_C46/5/P643//C643X5L8///</c>. Eine feste Fundstelle und
    /// damit deutlich belastbarer als freies Durchsuchen nach Zeichenketten.
    /// </summary>
    private static string? ReadVariant(byte[] data, long blockStart, long blockSize)
    {
        long at = blockStart + VariantOffset;
        if (VariantOffset + 8 > blockSize || at + 8 > data.LongLength) return null;

        var text = new StringBuilder();
        for (long i = at; i < Math.Min(at + 128, blockStart + blockSize); i++)
        {
            byte b = data[i];
            if (b is 0x00 or 0xFF) break;
            if (b < 0x20 || b > 0x7E) return null;
            text.Append((char)b);
        }

        string value = text.ToString();
        return value.Contains('/') && value.Length >= 8 ? value : null;
    }

    // ==================================================================
    // Kettenlauf — die Gegenprobe
    // ==================================================================

    /// <summary>
    /// Läuft die Kette über <c>nextSector</c> ab. Höchstens
    /// <see cref="MaxChainSteps"/> Schritte, besuchte Adressen in einem
    /// HashSet — eine zyklische oder überlange Kette bricht ab und wird als
    /// solche gemeldet.
    /// </summary>
    private static List<BoschBlock> WalkChain(byte[] data, PhysicalLayout layout,
                                              List<BoschBlock> scanned, List<string> evidence)
    {
        var chain = new List<BoschBlock>();

        long? start = layout.ToFile(EntryPoint) is { } entry &&
                      TryReadHeader(data, layout, entry, out _)
            ? entry
            : null;

        if (start is null && scanned.Count > 0)
        {
            start = scanned[0].FileStart;
            evidence.Add($"Kein Blockkopf am üblichen Einstiegspunkt {Hex.Addr(EntryPoint)} — " +
                         $"Kettenlauf beginnt beim ersten abgetasteten Block {Hex.Addr(start.Value)}");
        }

        if (start is null) return chain;

        var visited = new HashSet<long>();
        long? cursor = start;

        for (int step = 0; cursor is { } fileStart; step++)
        {
            if (step >= MaxChainSteps)
            {
                evidence.Add($"Kettenlauf nach {MaxChainSteps} Schritten abgebrochen — " +
                             "die Kette ist überlang");
                break;
            }

            if (!visited.Add(fileStart))
            {
                evidence.Add($"Kette läuft im Kreis: {Hex.Addr(fileStart)} war schon dran — abgebrochen");
                break;
            }

            if (!TryReadHeader(data, layout, fileStart, out var block) || block is null)
            {
                evidence.Add($"Kette bricht ab: bei {Hex.Addr(fileStart)} steht kein gültiger Blockkopf");
                break;
            }

            chain.Add(block with { ChainOrder = chain.Count + 1 });

            if (block.NextCpu is not { } nextCpu) break;   // nextSector == 0: sauberes Kettenende

            if (layout.ToFile(nextCpu) is not { } nextFile)
            {
                evidence.Add($"Kette bricht ab: {Hex.Addr(nextCpu)} liegt außerhalb des Abbilds");
                break;
            }
            cursor = nextFile;
        }

        return chain;
    }

    private static void CompareMethods(List<BoschBlock> scanned, List<BoschBlock> chain,
                                       List<string> evidence)
    {
        var scannedStarts = scanned.Select(b => b.FileStart).ToHashSet();
        var chainStarts = chain.Select(b => b.FileStart).ToHashSet();

        if (chain.Count > 0 && scannedStarts.SetEquals(chainStarts))
        {
            evidence.Add($"Abtastung und Kettenlauf liefern dieselben {chain.Count} Blöcke — " +
                         "zwei unabhängige Verfahren, ein Ergebnis");
            return;
        }

        foreach (long only in scannedStarts.Except(chainStarts).Order())
            evidence.Add($"Block bei {Hex.Addr(only)} nur von der Abtastung gefunden, " +
                         "nicht über die Kette erreichbar");

        foreach (long only in chainStarts.Except(scannedStarts).Order())
            evidence.Add($"Block bei {Hex.Addr(only)} nur über die Kette erreicht, " +
                         "von der Abtastung nicht bestätigt");
    }

    private static List<BoschBlock> ApplyChainOrder(List<BoschBlock> scanned, List<BoschBlock> chain)
    {
        var order = chain.ToDictionary(b => b.FileStart, b => b.ChainOrder);

        return scanned
            .Select(b => order.TryGetValue(b.FileStart, out var position) ? b with { ChainOrder = position } : b)
            .OrderBy(b => b.FileStart)
            .ToList();
    }

    // ==================================================================
    // Prüfsummen nachrechnen — der eigentliche Beleg
    // ==================================================================

    /// <summary>
    /// Rechnet jede Prüfsummenstruktur des Blocks nach. Die Struktur ist
    /// 32 Byte groß und beginnt bei +0x34; danach folgt das Prüfwort des Blocks.
    ///
    /// <strong>Gemessen:</strong> <c>csEnd</c> zeigt auf das <em>letzte Byte</em>
    /// des geprüften Bereichs, die Länge ist also <c>csEnd - csStart + 1</c>.
    /// <c>blockEnd</c> folgt einer anderen Konvention — es zeigt auf den
    /// <c>0xDEADBEEF</c>-Abschluss, also auf das letzte <em>Wort</em>. Der
    /// naheliegende Analogieschluss von <c>blockEnd</c> auf <c>csEnd</c> ist
    /// damit falsch; er stand hier und wurde widerlegt.
    ///
    /// Beleg: über fünf EDC17-Abbilder (Audi A4, VW Touran, Porsche Panamera,
    /// zwei weitere) gehen mit <c>+1</c> 57 von 59 Strukturen auf, mit <c>+4</c>
    /// nur 11 — und die 11 sind ausschließlich <c>ADD32</c>-Fälle, deren
    /// Wortschleife die drei überzähligen Bytes ohnehin nicht liest. Der erste
    /// Block eines Abbilds macht es unmittelbar sichtbar: <c>csStart</c>
    /// 0x80000000, <c>csEnd</c> 0x8000FFFB, <c>0xDEADBEEF</c> bei Datei-Offset
    /// 0xFFFC — der geprüfte Bereich ist der Blockinhalt ohne den Abschluss,
    /// und 0xFFFB ist keine Wortgrenze.
    ///
    /// Die beiden zunächst offenen <c>ADD16</c>-Abweichungen sind inzwischen
    /// geklärt — ihr letztes Wort zählt verschoben, siehe
    /// <see cref="BoschChecksum.Add16"/>. Damit gehen alle 59 Strukturen der
    /// fünf Abbilder auf, bis auf den Dataset-Block eines nachweislich
    /// veränderten Abbilds.
    /// </summary>
    public static List<BoschChecksumStructure> VerifyChecksums(byte[] data, PhysicalLayout layout,
                                                               BoschBlock block)
    {
        var result = new List<BoschChecksumStructure>();

        for (int i = 0; i < block.ChecksumStructureCount; i++)
        {
            long at = block.FileStart + HeaderSize + i * (long)ChecksumStructureSize;
            if (at + ChecksumStructureSize > data.LongLength) break;

            byte csBlockId = data[at];
            long csStart = ByteOrder.ReadUInt32(data, at + 0x04, Endianness.Little);
            long csEnd = ByteOrder.ReadUInt32(data, at + 0x08, Endianness.Little);
            uint startValue = ByteOrder.ReadUInt32(data, at + 0x0C, Endianness.Little);
            uint expected = ByteOrder.ReadUInt32(data, at + 0x10, Endianness.Little);
            uint blockIdRef = ByteOrder.ReadUInt32(data, at + 0x14, Endianness.Little);
            uint blockIdAddr = ByteOrder.ReadUInt32(data, at + 0x18, Endianness.Little);
            byte algorithm = (byte)(ByteOrder.ReadUInt16(data, at + 0x1C, Endianness.Little) & 0xFF);

            result.Add(Evaluate(data, layout, csBlockId, csStart, csEnd, startValue, expected,
                                blockIdRef, blockIdAddr, algorithm));
        }

        return result;
    }

    private static BoschChecksumStructure Evaluate(byte[] data, PhysicalLayout layout,
                                                   byte csBlockId, long csStart, long csEnd,
                                                   uint startValue, uint expected,
                                                   uint blockIdRef, uint blockIdAddr, byte algorithm)
    {
        BoschChecksumStructure Unchecked(string note) =>
            new(csBlockId, csStart, csEnd, startValue, expected, blockIdRef, blockIdAddr,
                algorithm, null, null, note);

        if (!BoschChecksum.IsKnown(algorithm))
            return Unchecked($"Algorithmus {BoschChecksum.Name(algorithm)} — nicht nachgerechnet, " +
                             "das Verfahren ist nicht bekannt");

        if (csEnd < csStart)
            return Unchecked("Bereich ist leer oder verdreht — nicht nachgerechnet");

        // csEnd ist das letzte Byte des Bereichs, nicht das letzte Wort.
        long length = csEnd - csStart + 1;

        if (layout.ToFile(csStart) is not { } from || layout.ToFile(csEnd) is not { } to)
            return Unchecked("Bereich liegt außerhalb des Abbilds — nicht nachgerechnet");

        if (to - from != csEnd - csStart || from + length > data.LongLength)
            return Unchecked("Bereich überspannt mehrere Bänke — nicht nachgerechnet");

        uint? computed = BoschChecksum.Compute(algorithm, data.AsSpan((int)from, (int)length), startValue);
        uint? target = BoschChecksum.Expected(algorithm, expected);

        if (computed is null || target is null)
            return Unchecked($"Algorithmus {BoschChecksum.Name(algorithm)} — nicht nachgerechnet");

        return new BoschChecksumStructure(csBlockId, csStart, csEnd, startValue, expected,
                                          blockIdRef, blockIdAddr, algorithm,
                                          computed, computed == target, "");
    }

    // ==================================================================
    // Lücken
    // ==================================================================

    /// <summary>Bereiche, die von keinem Block belegt sind. Beobachtung, keine Deutung.</summary>
    public static List<(long From, long To)> Gaps(IReadOnlyList<BoschBlock> blocks, long imageSize)
    {
        var claimed = blocks.Select(b => (b.FileStart, b.FileEnd))
                            .OrderBy(r => r.FileStart)
                            .ToList();

        return BinaryHeuristics.Subtract(0, imageSize, claimed).ToList();
    }

    // ==================================================================
    // CVN
    // ==================================================================

    /// <summary>
    /// Sucht die Konfigurationsstruktur der CVN: vier aufeinanderfolgende Wörter
    /// <c>{Zeiger, DS_START, DS_WOCS_END, Anzahl}</c>, wobei <c>DS_START</c> die
    /// Startadresse des Dataset-Blocks ist. Der Zeiger führt auf eine Tabelle
    /// aus <c>(Start, Ende)</c>-Paaren, über die die CRC32 läuft.
    ///
    /// Wird sie nicht gefunden, gibt es keinen Ratewert und keine Null, sondern
    /// gar keine CVN.
    ///
    /// <strong>Offener Punkt: es kann mehrere geben.</strong> Der Bosch-Funktionsrahmen
    /// für MED17.5 spricht im Kapitel zu OBD-Mode $09 durchgehend im Plural —
    /// „die Anzahl der Antwortbotschaften ist abhängig von der Anzahl der
    /// CVNunknowns“, und über CAN werden „alle CVNunknowns in einer einzigen
    /// Botschaft gesendet“. Ein Steuergerät kann also mehr als eine CVN melden.
    /// Diese Suche bricht beim ersten Fund ab und gibt genau eine zurück.
    ///
    /// Ob die weiteren CVNs überhaupt eine eigene Konfigurationsstruktur dieser
    /// Form im Abbild haben, ist ungeprüft — in den fünf ausgewerteten Abbildern
    /// wurde nicht danach gesucht. Solange das offen ist, wäre „die CVN“ im
    /// Plural auszugeben eine Behauptung; der Einzelwert ist belegt, seine
    /// Vollständigkeit nicht. Wer das entscheidet, braucht ein Abbild, dessen
    /// Diagnose nachweislich mehrere CVNs meldet.
    ///
    /// Nebenbefund aus derselben Quelle: eine CVN kann kürzer als vier Byte
    /// sein — sie wird dann mit vorangestellten Füllbytes übertragen. Der hier
    /// gerechnete Wert ist immer eine volle CRC32.
    /// </summary>
    public static BoschCvn? FindCvn(byte[] data, PhysicalLayout layout, BoschBlock dataset)
    {
        long datasetCpu = PhysicalLayout.Normalize(dataset.CpuStart);

        for (long at = 0; at + 16 <= data.LongLength; at += 4)
        {
            long pointer = ByteOrder.ReadUInt32(data, at, Endianness.Little);
            long start = ByteOrder.ReadUInt32(data, at + 4, Endianness.Little);
            long end = ByteOrder.ReadUInt32(data, at + 8, Endianness.Little);
            long count = ByteOrder.ReadUInt32(data, at + 12, Endianness.Little);

            if (PhysicalLayout.Normalize(start) != datasetCpu) continue;
            if (end <= start || count is < 1 or > 64) continue;
            if (layout.ToFile(pointer) is not { } tableFile) continue;
            if (tableFile + count * 8 > data.LongLength) continue;

            var ranges = ReadCvnRanges(data, layout, tableFile, (int)count);
            if (ranges is null) continue;

            uint crc = BoschChecksum.CvnStart;
            foreach (var (from, to) in ranges)
            {
                long fileFrom = layout.ToFile(from)!.Value;
                crc = BoschChecksum.CvnUpdate(crc, data.AsSpan((int)fileFrom, (int)(to - from + 4)));
            }

            return new BoschCvn(BoschChecksum.CvnFinish(crc), ranges, at);
        }

        return null;
    }

    private static List<(long Start, long End)>? ReadCvnRanges(byte[] data, PhysicalLayout layout,
                                                               long tableFile, int count)
    {
        var ranges = new List<(long, long)>(count);

        for (int i = 0; i < count; i++)
        {
            long from = ByteOrder.ReadUInt32(data, tableFile + i * 8L, Endianness.Little);
            long to = ByteOrder.ReadUInt32(data, tableFile + i * 8L + 4, Endianness.Little);

            if (to < from) return null;
            if (layout.ToFile(from) is not { } fileFrom) return null;
            if (layout.ToFile(to) is not { } fileTo) return null;
            if (fileTo - fileFrom != to - from) return null;
            if (fileFrom + (to - from) + 4 > data.LongLength) return null;

            ranges.Add((from, to));
        }

        return ranges;
    }

    // ==================================================================
    // Übergabe an das Sektormodell
    // ==================================================================

    /// <summary>
    /// Macht aus jedem bestätigten Block einen <see cref="SectorInfo"/>, damit
    /// er extrahiert und als S-Record exportiert werden kann.
    ///
    /// <c>Verified</c> bekommt nur, wessen Prüfsummen aufgehen. Ein Block mit
    /// gültigem Kopf, aber abweichender Prüfsumme wird <c>CrcMismatch</c> — ein
    /// Abbild mit bearbeiteter Kalibrierung sieht damit sofort so aus, wie es ist.
    /// </summary>
    public static List<SectorInfo> ToSectors(IReadOnlyList<BoschBlock> blocks)
    {
        var sectors = new List<SectorInfo>();

        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var failed = block.Checksums.FirstOrDefault(c => c.Ok == false);
            var any = failed ?? block.Checksums.FirstOrDefault();

            sectors.Add(new SectorInfo
            {
                Kind = KindOf(block.Id),
                Label = block.IdName + (block.Otp ? " · OTP" : ""),
                Prefix = $"blk{i + 1:00}_",
                Start = block.FileStart,
                CpuOffset = block.CpuStart,
                Status = failed is null ? SectorStatus.Verified : SectorStatus.CrcMismatch,
                PartNumber = block.Identifier.Length > 0 ? block.Identifier : $"0x{block.Id:X2}",
                Length = block.Size,
                CrcStored = any is null ? 0 : BoschChecksum.Expected(any.Algorithm, any.ExpectedValue) ?? 0,
                CrcComputed = any?.Computed ?? 0,
                Writable = false,
                HeaderFields = DescribeHeader(block)
            });
        }

        return sectors;
    }

    private static SectorKind KindOf(byte id) => id switch
    {
        0x10 or 0xF0 => SectorKind.Bootloader,
        0x60 or 0x70 or 0x80 => SectorKind.Dataset,
        0x40 or 0x50 or 0xA0 or 0xB0 => SectorKind.Asw,
        _ => SectorKind.Other
    };

    private static Dictionary<string, string> DescribeHeader(BoschBlock block)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Blockart"] = $"0x{block.Id:X2} — {block.IdName}",
            ["Kennungswort"] = $"0x{block.Flags | block.Id:X8}" + (block.Otp ? "  (OTP gesetzt)" : ""),
            ["Größe"] = $"{block.Size:N0} B",
            ["blockEnd"] = Hex.Addr(block.CpuEnd),
            ["nextSector"] = block.NextCpu is { } next ? Hex.Addr(next) : "0 — Kettenende",
            ["swIdentifier"] = block.Identifier,
            ["+0x24 (unerklärt)"] = string.Join(' ', block.UnknownBytes.Select(b => b.ToString("X2"))),
            ["checksumAdjust"] = $"0x{block.ChecksumAdjust:X8}",
            ["Prüfsummenstrukturen"] = block.ChecksumStructureCount.ToString()
        };

        if (block.Table1.Count > 0)
            fields["table1"] = $"{Hex.Addr(block.Table1Cpu)}: " +
                               string.Join(' ', block.Table1.Select(v => $"{v:X8}"));
        if (block.Table2.Count > 0)
            fields["table2"] = $"{Hex.Addr(block.Table2Cpu)}: " +
                               string.Join(' ', block.Table2.Select(v => $"{v:X8}"));
        if (block.Variant is { } variant)
            fields["Variante (+0x78)"] = variant;

        return fields;
    }
}
