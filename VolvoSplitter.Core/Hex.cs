namespace VolvoSplitter.Core;

/// <summary>
/// Adressdarstellung an einer Stelle. Datei-Offsets bleiben sechsstellig, wie
/// bisher; CPU-Adressen der TriCore-Bausteine (<c>0x80000000</c>, <c>0xAF000000</c>)
/// brauchen acht Stellen und bekommen sie auch.
///
/// <c>X6</c> schneidet nichts ab — <c>$"{0xA0000000:X6}"</c> liefert
/// <c>A0000000</c>. Die Fallunterscheidung dient allein der Ausrichtung.
/// </summary>
public static class Hex
{
    public static string Addr(long value) =>
        value is >= 0 and <= 0xFFFFFF ? $"0x{value:X6}" : $"0x{value:X8}";

    public static string Range(long start, long end) => $"{Addr(start)} – {Addr(end)}";
}
