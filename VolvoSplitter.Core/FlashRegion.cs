using System.Text;

namespace VolvoSplitter.Core;

public enum RegionKind
{
    /// <summary>Programmcode im Klartext — Zeichenketten und wiederkehrende Befehlsmuster.</summary>
    Code,

    /// <summary>Hohe Entropie ohne Struktur — verschlüsselt oder signiert.</summary>
    Opaque,

    /// <summary>Belegt, aber weder erkennbarer Code noch verschlüsselt.</summary>
    Data,

    /// <summary>Jenseits des Flash-Bausteins — der angehängte EEPROM-Auszug.</summary>
    Eeprom
}

/// <summary>Ein belegter Bereich des Abbilds, der keinen Sektorkopf trägt.</summary>
public sealed record FlashRegion(long Start, long Length, RegionKind Kind, string Description, double Entropy)
{
    public long End => Start + Length;
    public string AddressRange => $"0x{Start:X6} – 0x{End:X6}";
    public string SizeText => $"{Length:N0} B";

    public string Label => Kind switch
    {
        RegionKind.Code => "Programmcode",
        RegionKind.Opaque => "Verschlüsselt",
        RegionKind.Eeprom => "EEPROM",
        _ => "Daten"
    };
}

/// <summary>
/// Findet die belegten Bereiche des Abbilds, die keinen Sektorkopf haben, und
/// ordnet sie ein. Damit ist sichtbar, was zwischen den Sektoren liegt —
/// vor allem der ASW-Code, der zwar keinen Kopf trägt, aber im Klartext dasteht.
/// </summary>
public static class RegionScanner
{
    private const int Page = 0x1000;

    /// <summary>Bereiche unter dieser Größe sind Rauschen und werden übergangen.</summary>
    private const long MinInteresting = 0x800;

    public static List<FlashRegion> Scan(byte[] data, long flashSize, IEnumerable<SectorInfo> sectors)
    {
        var occupied = OccupiedRuns(data);
        var claimed = sectors.Where(s => s.Present)
                             .Select(s => (s.Start, s.End))
                             .OrderBy(s => s.Start)
                             .ToList();

        var regions = new List<FlashRegion>();
        foreach (var (start, end) in occupied)
            foreach (var (from, to) in Subtract(start, end, claimed))
            {
                // Das Seitenraster lässt hinter einem Sektor den Rest der
                // letzten Seite übrig — reines 0xFF. Weg damit.
                var (a, b) = TrimErased(data, from, to);
                if (b - a >= MinInteresting)
                    regions.Add(Describe(data, a, b - a, flashSize));
            }

        return regions;
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

    private static FlashRegion Describe(byte[] data, long start, long length, long flashSize)
    {
        if (start >= flashSize)
            return new FlashRegion(start, length, RegionKind.Eeprom,
                                   "Angehängter EEPROM-Auszug, liegt hinter dem Flash-Baustein", 0);

        double entropy = Entropy(data, start, length);
        double duplicates = DuplicateRatio(data, start, length);
        bool sourcePaths = ContainsSourcePaths(data, start, length);

        if (entropy < 0.5)
            return new FlashRegion(start, length, RegionKind.Data,
                $"Konstantes Füllbyte 0x{data[start]:X2}", entropy);

        if (entropy >= 7.9 && !sourcePaths)
            return new FlashRegion(start, length, RegionKind.Opaque,
                $"Entropie {entropy:0.00} — verschlüsselt oder signiert, kein lesbarer Inhalt", entropy);

        // Wiederkehrende Muster allein reichen nicht: Tabellen wiederholen sich
        // noch stärker als Code, haben aber deutlich weniger Entropie.
        if (sourcePaths || (duplicates > 0.02 && entropy >= 4.5))
            return new FlashRegion(start, length, RegionKind.Code,
                sourcePaths
                    ? $"Entropie {entropy:0.00}, enthält Quelldateipfade — Programmcode im Klartext"
                    : $"Entropie {entropy:0.00}, {duplicates:P0} wiederkehrende Befehlsmuster — Programmcode",
                entropy);

        return new FlashRegion(start, length, RegionKind.Data,
            $"Entropie {entropy:0.00}, {duplicates:P0} wiederkehrende Blöcke — Daten ohne Sektorkopf",
            entropy);
    }

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
