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
    public string AddressRange => $"0x{Start:X6} – 0x{End:X6}";
    public string SizeText => $"{Length:N0} B";

    /// <summary>Rekonstruierte CPU-Adresse, falls der Bereich einer Partition zuzuordnen ist.</summary>
    public long? CpuStart { get; init; }

    /// <summary>Physischer Block, in dem der Bereich liegt.</summary>
    public string? PartitionLabel { get; init; }

    /// <summary>Genauere Bezeichnung, die das allgemeine Label der Art ersetzt.</summary>
    public string? Title { get; init; }

    public RegionConfidence Confidence { get; init; } = RegionConfidence.Strong;

    public string? CpuAddressRange =>
        CpuStart is { } cpu ? $"0x{cpu:X6} – 0x{cpu + Length:X6}" : null;

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
    private const int Page = 0x1000;

    /// <summary>Bereiche unter dieser Größe sind Rauschen und werden übergangen.</summary>
    private const long MinInteresting = 0x800;

    public static List<FlashRegion> Scan(byte[] data, long flashSize, IEnumerable<SectorInfo> sectors,
                                         Mpc5777cLayout? layout = null)
    {
        var all = sectors as IReadOnlyList<SectorInfo> ?? sectors.ToList();

        var occupied = OccupiedRuns(data);
        var claimed = all.Where(s => s.Present)
                         .Select(s => (s.Start, s.End))
                         .OrderBy(s => s.Start)
                         .ToList();

        var fragments = new List<(long From, long To)>();
        foreach (var (start, end) in occupied)
            foreach (var (from, to) in Subtract(start, end, claimed))
            {
                // Das Seitenraster lässt hinter einem Sektor den Rest der
                // letzten Seite übrig — reines 0xFF. Weg damit.
                var (a, b) = TrimErased(data, from, to);
                if (b > a) fragments.Add((a, b));
            }

        var regions = new List<FlashRegion>();
        foreach (var (from, to) in MergePerEepromBlock(fragments, claimed, layout))
            if (to - from >= MinInteresting)
                regions.Add(Describe(data, from, to - from, flashSize, all, layout));

        return regions;
    }

    /// <summary>
    /// In den Low/Mid-Blöcken ist der Inhalt ein logisches Ganzes: Kopf am
    /// Blockanfang, angehängte Records dahinter, dazwischen und danach
    /// gelöschter Platz (NXP AN4868). Als einzelne Bruchstücke betrachtet
    /// wären Kopf und Records jeder für sich zu klein, um überhaupt gemeldet
    /// zu werden — der Block fiele stillschweigend unter den Tisch. Deshalb
    /// wird je Block vom ersten bis zum letzten belegten Byte zusammengefasst.
    /// </summary>
    private static List<(long From, long To)> MergePerEepromBlock(
        List<(long From, long To)> fragments, List<(long Start, long End)> claimed,
        Mpc5777cLayout? layout)
    {
        if (layout is null) return fragments;

        var spans = new Dictionary<long, (long From, long To)>();
        var result = new List<(long From, long To)>();

        foreach (var fragment in fragments)
        {
            var partition = layout.PartitionAt(fragment.From);

            // Nur echte EEPROM-Emulationsblöcke, und nur solange dort kein
            // Sektor liegt — sonst würde das Zusammenfassen ihn überspannen.
            if (partition is not { Type: FlashBlockType.Low or FlashBlockType.Mid } ||
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

    /// <summary>Schneidet gelöschte Bytes an beiden Enden ab.</summary>
    private static (long From, long To) TrimErased(byte[] data, long from, long to)
    {
        while (from < to && data[from] == 0xFF) from++;
        while (to > from && data[to - 1] == 0xFF) to--;
        return (from, to);
    }

    /// <summary>Zusammenhängende Bereiche, die nicht komplett auf 0xFF stehen.</summary>
    private static List<(long Start, long End)> OccupiedRuns(byte[] data)
    {
        var runs = new List<(long, long)>();
        long? current = null;

        for (long page = 0; page < data.LongLength; page += Page)
        {
            int length = (int)Math.Min(Page, data.LongLength - page);
            bool erased = IsErased(data, page, length);

            if (!erased) current ??= page;
            else if (current is { } start) { runs.Add((start, page)); current = null; }
        }
        if (current is { } last) runs.Add((last, data.LongLength));

        return runs;
    }

    private static bool IsErased(byte[] data, long offset, int length)
    {
        for (int i = 0; i < length; i++)
            if (data[offset + i] != 0xFF) return false;
        return true;
    }

    /// <summary>Die Teile von [start,end), die von keinem Sektor belegt sind.</summary>
    private static IEnumerable<(long From, long To)> Subtract(long start, long end,
                                                              List<(long Start, long End)> claimed)
    {
        long cursor = start;
        foreach (var (cs, ce) in claimed)
        {
            if (ce <= cursor || cs >= end) continue;
            if (cs > cursor) yield return (cursor, Math.Min(cs, end));
            cursor = Math.Max(cursor, ce);
            if (cursor >= end) yield break;
        }
        if (cursor < end) yield return (cursor, end);
    }

    // ------------------------------------------------------------------
    // Einordnung
    // ------------------------------------------------------------------

    private static FlashRegion Describe(byte[] data, long start, long length, long flashSize,
                                        IReadOnlyList<SectorInfo> sectors, Mpc5777cLayout? layout)
    {
        var partition = layout?.PartitionAt(start);
        var region = Classify(data, start, length, sectors, partition);

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
                                        IReadOnlyList<SectorInfo> sectors, FlashPartition? partition)
    {
        // 1. Ausführbares VOLVOECU-Modul — eigener Kopf, eigene CRC über den
        //    ganzen Block. Das ist kein EEPROM-Datensatz.
        if (VolvoEcuBlock.TryParse(data, start, out var ecu) && ecu is not null)
            return new FlashRegion(start, length, RegionKind.Code,
                VolvoEcuDescription(ecu), Entropy(data, start, length))
            {
                Title = "Low-Flash-Code / VOLVOECU-Modul",
                Confidence = ecu.CrcOk ? RegionConfidence.Confirmed : RegionConfidence.Strong
            };

        double entropy = Entropy(data, start, length);
        double duplicates = DuplicateRatio(data, start, length);
        bool sourcePaths = ContainsSourcePaths(data, start, length);

        // 2. Laufzeitveränderte NVM-Daten. Nur in den Low/Mid-Blöcken, denn nur
        //    dort betreibt das Steuergerät die EEPROM-Emulation.
        if (partition is { Type: FlashBlockType.Low or FlashBlockType.Mid } &&
            NvmEvidence(data, start, length, sectors) is { Count: > 0 } evidence)
            return new FlashRegion(start, length, RegionKind.NvmData,
                string.Join("; ", evidence), entropy)
            {
                Confidence = RegionConfidence.Strong
            };

        if (entropy < 0.5)
            return new FlashRegion(start, length, RegionKind.Data,
                $"Konstantes Füllbyte 0x{data[start]:X2}", entropy)
            {
                Confidence = RegionConfidence.Confirmed
            };

        // Hohe Entropie ist eine Beobachtung, kein Nachweis: Chiffretext,
        // komprimierte Daten und signierte Container sehen gleich aus.
        if (entropy >= 7.9 && !sourcePaths)
            return new FlashRegion(start, length, RegionKind.Opaque,
                $"Entropie {entropy:0.00}, kein bekannter Kopf — Format unbekannt; " +
                "Verschlüsselung oder Kompression möglich, aber nicht belegt", entropy)
            {
                Confidence = RegionConfidence.Unknown
            };

        // Wiederkehrende Muster allein reichen nicht: Tabellen wiederholen sich
        // noch stärker als Code, haben aber deutlich weniger Entropie.
        if (sourcePaths || (duplicates > 0.02 && entropy >= 4.5))
            return new FlashRegion(start, length, RegionKind.Code,
                sourcePaths
                    ? $"Entropie {entropy:0.00}, enthält Quelldateipfade — Programmcode im Klartext"
                    : $"Entropie {entropy:0.00}, {duplicates:P0} wiederkehrende Befehlsmuster — Programmcode",
                entropy)
            {
                Confidence = sourcePaths ? RegionConfidence.Confirmed : RegionConfidence.Strong
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
                                            IReadOnlyList<SectorInfo> sectors)
    {
        var evidence = new List<string>();
        var window = data.AsSpan((int)start, (int)length);

        int uptime = CountOccurrences(window, "UPTIME"u8);
        if (uptime > 0)
            evidence.Add($"{uptime} × UPTIME-Record angehängt — Beleg für Laufzeitänderungen");

        if (BlockStatus(window) is { } status)
            evidence.Add($"Blockstatus {status} am Blockanfang (AN4868)");

        foreach (var sector in sectors)
        {
            if (!sector.Present) continue;

            int at = IndexOfUInt32Be(window, sector.CrcStored);
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

    private static int CountOccurrences(ReadOnlySpan<byte> window, ReadOnlySpan<byte> needle)
    {
        int count = 0, cursor = 0;
        while (cursor < window.Length)
        {
            int hit = window[cursor..].IndexOf(needle);
            if (hit < 0) break;
            count++;
            cursor += hit + needle.Length;
        }
        return count;
    }

    private static int IndexOfUInt32Be(ReadOnlySpan<byte> window, uint value)
    {
        Span<byte> pattern =
        [
            (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
        ];
        return window.IndexOf(pattern);
    }

    // ------------------------------------------------------------------
    // Heuristiken
    // ------------------------------------------------------------------

    private static double Entropy(byte[] data, long start, long length)
    {
        var histogram = new long[256];
        for (long i = 0; i < length; i++) histogram[data[start + i]]++;

        double sum = 0;
        foreach (long count in histogram)
        {
            if (count == 0) continue;
            double p = (double)count / length;
            sum -= p * Math.Log2(p);
        }
        return sum;
    }

    /// <summary>
    /// Anteil wiederkehrender 16-Byte-Blöcke. Compilierter Code wiederholt sich
    /// stark, Chiffretext praktisch nie. Gemessen wird ein zusammenhängendes
    /// Fenster von höchstens 1 MiB — gestreute Stichproben zerstören genau die
    /// örtliche Wiederholung, auf die es hier ankommt.
    /// </summary>
    private static double DuplicateRatio(byte[] data, long start, long length)
    {
        const int Width = 16;
        const long WindowLimit = 1 << 20;

        long blocks = Math.Min(length, WindowLimit) / Width;
        if (blocks < 32) return 0;

        var seen = new HashSet<(long, long)>();
        int duplicates = 0;

        for (long b = 0; b < blocks; b++)
        {
            long offset = start + b * Width;
            var key = (BitConverter.ToInt64(data, (int)offset),
                       BitConverter.ToInt64(data, (int)offset + 8));
            if (!seen.Add(key)) duplicates++;
        }
        return (double)duplicates / blocks;
    }

    /// <summary>Assert-Strings mit Quelldateipfaden verraten unverschlüsselten Code.</summary>
    private static bool ContainsSourcePaths(byte[] data, long start, long length)
    {
        ReadOnlySpan<byte> needle = "src/"u8;
        ReadOnlySpan<byte> alternative = "../"u8;

        // Der gesamte Bereich, nicht nur der Anfang: im EMS2.3-ASW steht der
        // erste Pfad erst 580 KB nach Bereichsbeginn.
        var window = data.AsSpan((int)start, (int)length);
        return window.IndexOf(needle) >= 0 || window.IndexOf(alternative) >= 0;
    }
}
