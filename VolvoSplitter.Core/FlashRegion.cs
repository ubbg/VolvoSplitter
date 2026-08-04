using System.Text;

namespace VolvoSplitter.Core;

public enum RegionKind
{
    /// <summary>Programmcode im Klartext — Zeichenketten und wiederkehrende Befehlsmuster.</summary>
    Code,

    /// <summary>Hohe Entropie ohne erkennbare Struktur; Format unbekannt.</summary>
    Opaque,

    /// <summary>Belegt, aber weder erkennbarer Code noch opak.</summary>
    Data,

    /// <summary>
    /// Laufzeitveränderte Daten der EEPROM-Emulation: angehängte Records,
    /// Prüfsummenkopien. Nicht „der EEPROM-Baustein" — der MPC5777C hat keinen.
    /// </summary>
    NvmData
}

/// <summary>Wie sicher die Einordnung ist.</summary>
public enum RegionConfidence
{
    /// <summary>Durch Kopf, Prüfsumme oder eindeutigen Inhalt belegt.</summary>
    Confirmed,

    /// <summary>Mehrere übereinstimmende Anzeichen, aber kein formaler Nachweis.</summary>
    Strong,

    /// <summary>Beobachtung ohne belastbare Deutung.</summary>
    Unknown
}

/// <summary>Ein belegter Bereich des Abbilds, der keinen Sektorkopf trägt.</summary>
public sealed record FlashRegion(long Start, long Length, RegionKind Kind, string Description, double Entropy)
{
    public long End => Start + Length;
    public string AddressRange => Hex.Range(Start, End);
    public string SizeText => $"{Length:N0} B";

    /// <summary>Rekonstruierte CPU-Adresse, falls der Bereich einer Partition zuzuordnen ist.</summary>
    public long? CpuStart { get; init; }

    /// <summary>Physischer Block, in dem der Bereich liegt.</summary>
    public string? PartitionLabel { get; init; }

    /// <summary>Genauere Bezeichnung, die das allgemeine Label der Art ersetzt.</summary>
    public string? Title { get; init; }

    public RegionConfidence Confidence { get; init; } = RegionConfidence.Strong;

    public string? CpuAddressRange =>
        CpuStart is { } cpu ? Hex.Range(cpu, cpu + Length) : null;

    public string Label => Title ?? Kind switch
    {
        RegionKind.Code => "Programmcode",
        RegionKind.Opaque => "Opaker Block",
        RegionKind.NvmData => "NVM-Daten",
        _ => "Daten"
    };

    public string ConfidenceText => Confidence switch
    {
        RegionConfidence.Confirmed => "gesichert",
        RegionConfidence.Strong => "stark gestützt",
        _ => "unbekannt"
    };

    /// <summary>Herkunftszeile für die Anzeige: physischer Block und CPU-Adresse.</summary>
    public string Origin => PartitionLabel switch
    {
        null => "",
        var label when CpuAddressRange is { } cpu => $"{label}   CPU {cpu}",
        var label => label
    };
}

/// <summary>
/// Findet die belegten Bereiche des Abbilds, die keinen Sektorkopf haben, und
/// ordnet sie ein. Damit ist sichtbar, was zwischen den Sektoren liegt —
/// vor allem der ASW-Code, der zwar keinen Kopf trägt, aber im Klartext dasteht.
///
/// Die Einordnung folgt dem Inhalt, nicht der Adresse. Ein Bereich hinter dem
/// Large Flash ist deshalb nicht automatisch EEPROM: dort liegen beim MPC5777C
/// vier gewöhnliche Flash-Blöcke, die ebenso gut ausführbaren Code enthalten
/// können — „EEPROM data" ist bei NXP nur die Spalte <em>Example use</em>.
/// </summary>
public static class RegionScanner
{
    /// <summary>Bereiche unter dieser Größe sind Rauschen und werden übergangen.</summary>
    private const long MinInteresting = 0x800;

    public static List<FlashRegion> Scan(byte[] data, long flashSize, IEnumerable<SectorInfo> sectors,
                                         PhysicalLayout? layout = null, EcuProfile? profile = null)
    {
        var all = sectors as IReadOnlyList<SectorInfo> ?? sectors.ToList();

        var occupied = BinaryHeuristics.OccupiedRuns(data);
        var claimed = all.Where(s => s.Present)
                         .Select(s => (s.Start, s.End))
                         .OrderBy(s => s.Start)
                         .ToList();

        var fragments = new List<(long From, long To)>();
        foreach (var (start, end) in occupied)
            foreach (var (from, to) in BinaryHeuristics.Subtract(start, end, claimed))
            {
                // Das Seitenraster lässt hinter einem Sektor den Rest der
                // letzten Seite übrig — reines 0xFF. Weg damit.
                var (a, b) = BinaryHeuristics.TrimErased(data, from, to);
                if (b > a) fragments.Add((a, b));
            }

        var regions = new List<FlashRegion>();
        foreach (var (from, to) in MergePerEepromBlock(fragments, claimed, layout))
            if (to - from >= MinInteresting)
                regions.Add(Describe(data, from, to - from, flashSize, all, layout, profile));

        return regions;
    }

    /// <summary>
    /// In den Blöcken der EEPROM-Emulation ist der Inhalt ein logisches Ganzes:
    /// Kopf am Blockanfang, angehängte Records dahinter, dazwischen und danach
    /// gelöschter Platz (NXP AN4868). Als einzelne Bruchstücke betrachtet
    /// wären Kopf und Records jeder für sich zu klein, um überhaupt gemeldet
    /// zu werden — der Block fiele stillschweigend unter den Tisch. Deshalb
    /// wird je Block vom ersten bis zum letzten belegten Byte zusammengefasst.
    /// </summary>
    private static List<(long From, long To)> MergePerEepromBlock(
        List<(long From, long To)> fragments, List<(long Start, long End)> claimed,
        PhysicalLayout? layout)
    {
        if (layout is null) return fragments;

        var spans = new Dictionary<long, (long From, long To)>();
        var result = new List<(long From, long To)>();

        foreach (var fragment in fragments)
        {
            var partition = layout.PartitionAt(fragment.From);

            // Nur echte EEPROM-Emulationsblöcke, und nur solange dort kein
            // Sektor liegt — sonst würde das Zusammenfassen ihn überspannen.
            if (partition is not { EmulatedEeprom: true } ||
                claimed.Any(c => c.Start < partition.FileEnd && c.End > partition.FileStart))
            {
                result.Add(fragment);
                continue;
            }

            long block = partition.FileStart;
            spans[block] = spans.TryGetValue(block, out var known)
                ? (Math.Min(known.From, fragment.From), Math.Max(known.To, fragment.To))
                : fragment;
        }

        result.AddRange(spans.Values);
        result.Sort((a, b) => a.From.CompareTo(b.From));
        return result;
    }

    // ------------------------------------------------------------------
    // Einordnung
    // ------------------------------------------------------------------

    private static FlashRegion Describe(byte[] data, long start, long length, long flashSize,
                                        IReadOnlyList<SectorInfo> sectors, PhysicalLayout? layout,
                                        EcuProfile? profile)
    {
        var partition = layout?.PartitionAt(start);
        var region = Classify(data, start, length, sectors, partition, profile);

        // Ohne Layout lässt sich über einen Anhang hinter dem Flash-Baustein
        // nichts Belastbares sagen — der Inhalt wird trotzdem eingeordnet.
        if (partition is null && start >= flashSize)
            region = region with
            {
                Description = region.Description + " · liegt hinter dem Flash-Baustein, Herkunft nicht bestimmt",
                Confidence = RegionConfidence.Unknown
            };

        return region with
        {
            CpuStart = partition?.ToCpu(start),
            PartitionLabel = partition?.Label
        };
    }

    private static FlashRegion Classify(byte[] data, long start, long length,
                                        IReadOnlyList<SectorInfo> sectors, FlashPartition? partition,
                                        EcuProfile? profile)
    {
        // 1. Ausführbares VOLVOECU-Modul — eigener Kopf, eigene CRC über den
        //    ganzen Block. Das ist kein EEPROM-Datensatz. Nur im TRW-Zweig:
        //    ein TriCore-Abbild wird gar nicht erst gegen ein Volvo-Format geprüft.
        if (profile is null or { Container: ContainerKind.TrwSector } &&
            VolvoEcuBlock.TryParse(data, start, out var ecu) && ecu is not null)
            return new FlashRegion(start, length, RegionKind.Code,
                VolvoEcuDescription(ecu), BinaryHeuristics.Entropy(data, start, length))
            {
                Title = "Low-Flash-Code / VOLVOECU-Modul",
                Confidence = ecu.CrcOk ? RegionConfidence.Confirmed : RegionConfidence.Strong
            };

        double entropy = BinaryHeuristics.Entropy(data, start, length);
        double duplicates = BinaryHeuristics.DuplicateRatio(data, start, length);
        string? sourcePath = FindSourcePath(data, start, length);

        // 2. Laufzeitveränderte NVM-Daten. Nur dort, wo das Steuergerät die
        //    EEPROM-Emulation überhaupt betreibt.
        if (partition is { EmulatedEeprom: true } &&
            NvmEvidence(data, start, length, sectors, partition) is { Count: > 0 } evidence)
            return new FlashRegion(start, length, RegionKind.NvmData,
                string.Join("; ", evidence), entropy)
            {
                Confidence = RegionConfidence.Strong
            };

        // Entropie unter 0,5 heißt „stark ungleichverteilt", nicht „konstant" —
        // die Konstanz wird deshalb an den Bytes geprüft und nicht aus der
        // Entropie gefolgert. Benannt wird das häufigste Byte, nicht das erste:
        // im VAG-Bestand traf data[start] zehnmal einen Ausreißer, fünfmal
        // einen, der im ganzen Bereich genau einmal vorkam (gemeldet war
        // „0x1D", der Bereich bestand zu 99,94 % aus 0x00).
        if (entropy < 0.5)
        {
            var (fill, count) = BinaryHeuristics.DominantByte(data, start, length);
            bool constant = count == length;

            return new FlashRegion(start, length, RegionKind.Data,
                constant
                    ? $"Konstantes Füllbyte 0x{fill:X2}"
                    : $"Überwiegend 0x{fill:X2} ({(double)count / length:P2}), " +
                      $"{length - count:N0} abweichende Bytes",
                entropy)
            {
                // „gesichert" trägt nur die nachgerechnete Konstanz. Sonst ist
                // es eine Beobachtung: die abweichenden Bytes sind belegt, und
                // was sie bedeuten, sagt dieser Bereich nicht.
                Confidence = constant ? RegionConfidence.Confirmed : RegionConfidence.Strong
            };
        }

        // Hohe Entropie ist eine Beobachtung, kein Nachweis: Chiffretext,
        // komprimierte Daten und signierte Container sehen gleich aus.
        //
        // Ein Textfund hebelt das nicht mehr aus: „Entropie 8,00" und
        // „Programmcode im Klartext" schließen einander aus. Steht in einem
        // solchen Bereich wirklich eine Zeichenkette, ist sie ein Bruchteil
        // davon — und „opak" bleibt die ehrlichere Aussage über das Ganze.
        if (entropy >= 7.9)
            return new FlashRegion(start, length, RegionKind.Opaque,
                $"Entropie {entropy:0.00}, kein bekannter Kopf — Format unbekannt; " +
                "Verschlüsselung oder Kompression möglich, aber nicht belegt", entropy)
            {
                Confidence = RegionConfidence.Unknown
            };

        // Wiederkehrende Muster allein reichen nicht: Tabellen wiederholen sich
        // noch stärker als Code, haben aber deutlich weniger Entropie.
        if (sourcePath is not null || (duplicates > 0.02 && entropy >= 4.5))
            return new FlashRegion(start, length, RegionKind.Code,
                sourcePath is not null
                    ? $"Entropie {entropy:0.00}, enthält Quelldateipfad »{sourcePath}« — Programmcode im Klartext"
                    : $"Entropie {entropy:0.00}, {duplicates:P0} wiederkehrende Befehlsmuster — Programmcode",
                entropy)
            {
                Confidence = sourcePath is not null ? RegionConfidence.Confirmed : RegionConfidence.Strong
            };

        return new FlashRegion(start, length, RegionKind.Data,
            $"Entropie {entropy:0.00}, {duplicates:P0} wiederkehrende Blöcke — Daten ohne Sektorkopf",
            entropy);
    }

    private static string VolvoEcuDescription(VolvoEcuBlock ecu)
    {
        var text = new StringBuilder("VOLVOECU-Modul");
        if (ecu.PartNumber.Length > 0) text.Append($", Teilenummer {ecu.PartNumber}");
        text.Append($", Code ab CPU 0x{ecu.CodeStart:X6}");
        text.Append(ecu.CrcOk
            ? $", CRC32 0x{ecu.CrcStored:X8} über den ganzen Block stimmt"
            : $", CRC32 0x{ecu.CrcStored:X8} weicht ab (berechnet 0x{ecu.CrcComputed:X8})");
        return text.ToString();
    }

    // ------------------------------------------------------------------
    // Anzeichen für laufzeitveränderte NVM-Daten
    // ------------------------------------------------------------------

    /// <summary>
    /// Sammelt Belege für EEPROM-Emulation nach dem Muster aus NXP AN4868:
    /// angehängte Records mit Klartextmarken, die dort beschriebenen
    /// Status-Doppelwörter am Blockanfang und Kopien bekannter Sektorprüfwerte.
    /// Jeder Beleg ist eine Beobachtung — die Volvo-eigenen Record-Felder sind
    /// nicht bekannt, deshalb wird hier nichts dekodiert.
    /// </summary>
    private static List<string> NvmEvidence(byte[] data, long start, long length,
                                            IReadOnlyList<SectorInfo> sectors,
                                            FlashPartition partition)
    {
        var evidence = new List<string>();
        var window = data.AsSpan((int)start, (int)length);

        int uptime = BinaryHeuristics.CountOccurrences(window, "UPTIME"u8);
        if (uptime > 0)
            evidence.Add($"{uptime} × UPTIME-Record angehängt — Beleg für Laufzeitänderungen");

        // Der AN4868-Blockstatus ist ein NXP-Vorschlagswert für die MPC57xx-
        // EEPROM-Emulation. Für den DFLASH eines Infineon-Bausteins gilt davon
        // nichts — deshalb bleibt die Prüfung an die Low/Mid-Blöcke gebunden.
        if (partition.Type is FlashBlockType.Low or FlashBlockType.Mid &&
            BlockStatus(window) is { } status)
            evidence.Add($"Blockstatus {status} am Blockanfang (AN4868)");

        foreach (var sector in sectors)
        {
            if (!sector.Present) continue;

            int at = ByteOrder.IndexOfUInt32(window, sector.CrcStored, Endianness.Big);
            if (at < 0) continue;

            // Der eigene Trailer des Sektors zählt nicht als Kopie.
            if (start + at >= sector.End - FlashFormat.CrcTrailerLen && start + at < sector.End) continue;

            evidence.Add($"Kopie des Prüfwerts 0x{sector.CrcStored:X8} von Sektor {sector.Label} bei +0x{at:X}");
        }

        return evidence;
    }

    /// <summary>
    /// Blockstatus-Doppelwort am Blockanfang laut AN4868, Tabelle 4 (MPC57xx,
    /// 8-Byte-ECC-Checkbase). Volvo verwendet diese Vorschlagswerte nicht
    /// zwingend — trifft nichts zu, ist das kein Gegenbeweis.
    /// </summary>
    private static string? BlockStatus(ReadOnlySpan<byte> window)
    {
        if (window.Length < 8) return null;

        ulong word = 0;
        for (int i = 0; i < 8; i++) word = (word << 8) | window[i];

        return word switch
        {
            0xFFFFFFFFFFFFFFFFul => "$erased",
            0x0000FFFFFFFFFFFFul => "$verified",
            0x00000000FFFFFFFFul => "$copy",
            0x000000000000FFFFul => "$active",
            _ => null
        };
    }

    /// <summary>
    /// Kürzester druckbarer ASCII-Lauf, der einen Nadeltreffer noch als Text
    /// gelten lässt.
    ///
    /// Gerechnet: 95 der 256 Bytewerte sind druckbar, also hat ein Lauf von 16
    /// in gleichverteilten Daten die Wahrscheinlichkeit (95/256)^16 ≈ 1,3·10⁻⁷ —
    /// unter einem erwarteten Treffer je 4 MiB. Gemessen: über 1516 echte
    /// VAG-EDC17-Abbilder ist der längste druckbare Lauf, der eine der drei
    /// Nadeln enthält, 8 Byte lang (»VV=VV../«) — kein einziger Zufallstreffer
    /// kommt der Schwelle nahe. Ein echter Pfad wie »../src/appl/main.c« hat 18.
    /// </summary>
    private const int MinPathRun = 16;

    /// <summary>So viel Fundtext steht im Bericht; mehr sprengt die Spalte.</summary>
    private const int PathSampleLen = 48;

    /// <summary>
    /// Assert-Strings mit Quelldateipfaden verraten unverschlüsselten Code.
    /// <c>@(#)</c> ist die SCCS-Kennung, die in Bosch-Ständen üblich ist und
    /// dieselbe Aussage trägt. Liefert den gefundenen Text, damit der Bericht
    /// den Beleg zeigen kann statt ihn zu behaupten.
    ///
    /// Entscheidend ist nicht die Nadel, sondern dass sie <em>in einer
    /// Zeichenkette</em> steht: drei Bytes kommen in mehreren MiB Binärdaten
    /// zwangsläufig vor. Über 1516 VAG-EDC17-Abbilder war das fünfmal der Fall
    /// und fünfmal falsch — viermal in einem Bereich mit Entropie 8,00, also
    /// mitten im Rauschen. »src/« und »@(#)« kommen dort in keinem einzigen
    /// Abbild vor; ausgelöst hat also ausschließlich der Zufallstreffer.
    /// </summary>
    private static string? FindSourcePath(byte[] data, long start, long length)
    {
        // Der gesamte Bereich, nicht nur der Anfang: im EMS2.3-ASW steht der
        // erste Pfad erst 580 KB nach Bereichsbeginn.
        var window = data.AsSpan((int)start, (int)length);

        return FindInText(window, "src/"u8)
            ?? FindInText(window, "../"u8)
            ?? FindInText(window, "@(#)"u8);
    }

    /// <summary>
    /// Erster Treffer der Nadel, der in einem hinreichend langen druckbaren Lauf
    /// steht — als Text. Gesucht wird über alle Vorkommen: der erste kann
    /// Zufall sein, während ein späterer im echten Textblock liegt.
    /// </summary>
    private static string? FindInText(ReadOnlySpan<byte> window, ReadOnlySpan<byte> needle)
    {
        int cursor = 0;
        while (cursor < window.Length)
        {
            int hit = window[cursor..].IndexOf(needle);
            if (hit < 0) return null;

            int at = cursor + hit;
            var (from, to) = BinaryHeuristics.PrintableRun(window, at);
            if (to - from >= MinPathRun)
                return Encoding.ASCII.GetString(window[from..Math.Min(to, from + PathSampleLen)]);

            cursor = at + 1;
        }
        return null;
    }
}
