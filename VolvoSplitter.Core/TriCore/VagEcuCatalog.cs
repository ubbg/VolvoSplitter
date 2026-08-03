namespace VolvoSplitter.Core.TriCore;

/// <summary>Ein Steuergerätetyp mit den Bausteinen, die dafür in Frage kommen.</summary>
public sealed record VagEcu(string Type, string Fuel, IReadOnlyList<string> Micros, string Note)
{
    public bool Ambiguous => Micros.Count != 1;

    public string Display => $"{Type} · {string.Join(" oder ", Micros)} · {Fuel}" +
                             (Note.Length > 0 ? $" · {Note}" : "");
}

/// <summary>
/// Zuordnung Steuergerätetyp → Mikrocontroller.
///
/// <strong>Herkunft: Nutzerangabe, kein Herstellerdokument.</strong> Die Tabelle
/// wurde als Teil der Aufgabenstellung übergeben und nicht gegen eine
/// Bosch- oder VAG-Unterlage geprüft. Sie ist deshalb kein Layout, sondern der
/// Entkoppler einer Mehrdeutigkeit: eine 2-MiB-Datei kann ein TC1796 oder die
/// PMU0 eines TC1797 sein, und die Sektorkarten ihrer oberen Hälften
/// unterscheiden sich. Findet der Kennungsleser <c>EDC17CP44</c>, ist es ein
/// TC1797; findet er <c>EDC17CP14</c>, ein TC1796. Ohne Fund bleibt es
/// mehrdeutig und wird so gemeldet.
/// </summary>
public static class VagEcuCatalog
{
    public static IReadOnlyList<VagEcu> All { get; } =
    [
        // --- Diesel ---
        new("EDC17U01",   "Diesel", ["TC1766"],            ""),
        new("EDC17U05",   "Diesel", ["TC1766"],            ""),
        new("EDC17CP04",  "Diesel", ["TC1796"],            ""),
        new("EDC17CP14",  "Diesel", ["TC1796"],            ""),
        new("EDC17CP20",  "Diesel", ["TC1796", "TC1797"],  ""),
        new("EDC17CP44",  "Diesel", ["TC1797"],            ""),
        new("EDC17C46",   "Diesel", ["TC1767"],            ""),
        new("EDC17C54",   "Diesel", ["TC1797"],            ""),
        new("EDC17C64",   "Diesel", ["TC1767", "TC1782"],  ""),
        new("EDC17C74",   "Diesel", ["TC1793", "TC1797"],  ""),

        // --- Benzin ---
        new("MED17.1.21", "Benzin", ["TC1793"],            ""),
        new("MED17.1.27", "Benzin", ["TC1793"],            ""),
        new("MED17.1.1",  "Benzin", ["TC1796"],            ""),
        new("MED17.1.6",  "Benzin", ["TC1797"],            ""),
        new("MED17.1",    "Benzin", ["TC1796"],            "externer Flash über die EBU möglich"),
        new("MED17.5.21", "Benzin", ["TC1782"],            ""),
        new("MED17.5.25", "Benzin", ["TC1782"],            ""),
        new("MED17.5.26", "Benzin", ["TC1782", "TC1793"],  ""),
        new("MED17.5.27", "Benzin", ["TC1782", "TC1793"],  ""),
        new("MED17.5.2",  "Benzin", ["TC1766", "TC1767"],  ""),
        new("MED17.5.5",  "Benzin", ["TC1766", "TC1767"],  ""),
        new("ME17.5",     "Benzin", ["TC1762", "TC1766"],  "")
    ];

    /// <summary>
    /// Sucht den Eintrag zu einem Typ aus dem Abbild. Verglichen wird auf der
    /// normalisierten Form, damit <c>EDC17_C46</c> und <c>EDC17 C46</c>
    /// denselben Eintrag treffen; der längste passende Eintrag gewinnt, damit
    /// <c>MED17.1.6</c> nicht als <c>MED17.1</c> durchgeht.
    /// </summary>
    public static VagEcu? Find(string? ecuType)
    {
        if (string.IsNullOrWhiteSpace(ecuType)) return null;

        string needle = Normalize(ecuType);

        return All.Where(e => needle.StartsWith(Normalize(e.Type), StringComparison.Ordinal))
                  .OrderByDescending(e => e.Type.Length)
                  .FirstOrDefault();
    }

    /// <summary>
    /// Der Baustein zu einem Steuergerätetyp — nur, wenn die Tabelle genau einen
    /// nennt. Bei zwei Kandidaten wird nichts gewählt: das wäre Raten.
    /// </summary>
    public static TriCoreDevice? DeviceFor(string? ecuType)
    {
        if (Find(ecuType) is not { Ambiguous: false } ecu) return null;
        return TriCoreDevice.ByName(ecu.Micros[0]);
    }

    private static string Normalize(string text)
    {
        var buffer = new char[text.Length];
        int length = 0;

        foreach (char c in text)
            if (char.IsLetterOrDigit(c) || c == '.')
                buffer[length++] = char.ToUpperInvariant(c);

        return new string(buffer, 0, length);
    }
}
