namespace VolvoSplitter.Core;

/// <summary>
/// Bausteinunabhängige Messungen an Rohdaten. Aus <see cref="RegionScanner"/>
/// herausgezogen, damit sie auch außerhalb der Bereichsanalyse benutzbar sind
/// und Löschbyte und Seitengröße Parameter statt Literale werden.
///
/// Jede Funktion hier liefert eine <em>Beobachtung</em>. Was sie bedeutet,
/// entscheidet die aufrufende Stelle.
/// </summary>
public static class BinaryHeuristics
{
    public const byte ErasedByte = 0xFF;
    public const int PageSize = 0x1000;

    /// <summary>Shannon-Entropie in Bit je Byte.</summary>
    public static double Entropy(byte[] data, long start, long length)
    {
        if (length <= 0) return 0;

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
    /// Häufigstes Byte des Bereichs und wie oft es vorkommt. Ist die Anzahl
    /// gleich der Länge, ist der Bereich konstant.
    ///
    /// Das ist genau die Aussage, die eine niedrige Entropie <em>nicht</em>
    /// liefert: 1,4 MiB aus 0x00 mit 500 abweichenden Bytes bleiben unter
    /// 0,5 Bit je Byte und sind trotzdem nicht konstant.
    /// </summary>
    public static (byte Value, long Count) DominantByte(byte[] data, long start, long length)
    {
        if (length <= 0) return (0, 0);

        var histogram = new long[256];
        for (long i = 0; i < length; i++) histogram[data[start + i]]++;

        byte best = 0;
        for (int value = 1; value < 256; value++)
            if (histogram[value] > histogram[best]) best = (byte)value;

        return (best, histogram[best]);
    }

    /// <summary>Druckbares ASCII — der Bereich, in dem Zeichenketten stehen.</summary>
    private static bool IsPrintable(byte b) => b is >= 0x20 and < 0x7F;

    /// <summary>
    /// Der zusammenhängende druckbare ASCII-Lauf, in dem <paramref name="at"/>
    /// liegt, als Halboffenintervall. Steht dort kein druckbares Byte, ist das
    /// Ergebnis leer.
    ///
    /// Damit lässt sich ein Textfund von einem Zufallstreffer trennen: eine
    /// kurze Bytefolge kommt in mehreren MiB Binärdaten zwangsläufig vor, ein
    /// Fund <em>innerhalb einer Zeichenkette</em> nicht.
    /// </summary>
    public static (int From, int To) PrintableRun(ReadOnlySpan<byte> window, int at)
    {
        if (at < 0 || at >= window.Length || !IsPrintable(window[at])) return (at, at);

        int from = at, to = at + 1;
        while (from > 0 && IsPrintable(window[from - 1])) from--;
        while (to < window.Length && IsPrintable(window[to])) to++;
        return (from, to);
    }

    /// <summary>
    /// Anteil wiederkehrender 16-Byte-Blöcke. Compilierter Code wiederholt sich
    /// stark, Chiffretext praktisch nie. Gemessen wird ein zusammenhängendes
    /// Fenster von höchstens 1 MiB — gestreute Stichproben zerstören genau die
    /// örtliche Wiederholung, auf die es hier ankommt.
    /// </summary>
    public static double DuplicateRatio(byte[] data, long start, long length)
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

    /// <summary>
    /// Anteil der 64-Byte-Fenster, die als u8- oder u16-Folge monoton nicht
    /// fallend sind. Kennfeldachsen und Stützstellen sind genau das — ein
    /// billiges Signal, das Datenbereiche von Code trennt.
    ///
    /// Ein Anzeichen, kein Nachweis: aufsteigende Zählwerte, Tabellen von
    /// Zeigern und Textblöcke können denselben Anteil erzeugen.
    /// </summary>
    public static double MonotonicRunRatio(byte[] data, long start, long length,
                                           Endianness order = Endianness.Little)
    {
        const int Window = 64;

        long windows = Math.Min(length, 1 << 20) / Window;
        if (windows < 8) return 0;

        int monotone = 0;
        for (long w = 0; w < windows; w++)
        {
            long at = start + w * Window;
            if (IsMonotoneBytes(data, at, Window) || IsMonotoneWords(data, at, Window, order))
                monotone++;
        }
        return (double)monotone / windows;
    }

    private static bool IsMonotoneBytes(byte[] data, long start, int length)
    {
        bool moved = false;
        for (int i = 1; i < length; i++)
        {
            if (data[start + i] < data[start + i - 1]) return false;
            if (data[start + i] != data[start + i - 1]) moved = true;
        }
        // Eine konstante Folge ist trivial monoton und sagt nichts aus.
        return moved;
    }

    private static bool IsMonotoneWords(byte[] data, long start, int length, Endianness order)
    {
        bool moved = false;
        ushort previous = ByteOrder.ReadUInt16(data, start, order);

        for (int i = 2; i < length; i += 2)
        {
            ushort current = ByteOrder.ReadUInt16(data, start + i, order);
            if (current < previous) return false;
            if (current != previous) moved = true;
            previous = current;
        }
        return moved;
    }

    /// <summary>Zusammenhängende Bereiche, die nicht komplett auf dem Löschbyte stehen.</summary>
    public static List<(long Start, long End)> OccupiedRuns(byte[] data, int page = PageSize,
                                                            byte erased = ErasedByte)
    {
        var runs = new List<(long, long)>();
        long? current = null;

        for (long at = 0; at < data.LongLength; at += page)
        {
            long length = Math.Min(page, data.LongLength - at);

            if (!IsErased(data, at, length, erased)) current ??= at;
            else if (current is { } start) { runs.Add((start, at)); current = null; }
        }
        if (current is { } last) runs.Add((last, data.LongLength));

        return runs;
    }

    public static bool IsErased(byte[] data, long offset, long length, byte erased = ErasedByte)
    {
        for (long i = 0; i < length; i++)
            if (data[offset + i] != erased) return false;
        return true;
    }

    /// <summary>Schneidet gelöschte Bytes an beiden Enden ab.</summary>
    public static (long From, long To) TrimErased(byte[] data, long from, long to,
                                                  byte erased = ErasedByte)
    {
        while (from < to && data[from] == erased) from++;
        while (to > from && data[to - 1] == erased) to--;
        return (from, to);
    }

    /// <summary>Die Teile von [start,end), die von keinem belegten Bereich beansprucht sind.</summary>
    public static IEnumerable<(long From, long To)> Subtract(long start, long end,
                                                             IReadOnlyList<(long Start, long End)> claimed)
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

    public static int CountOccurrences(ReadOnlySpan<byte> window, ReadOnlySpan<byte> needle)
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
}
