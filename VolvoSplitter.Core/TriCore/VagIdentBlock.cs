using System.Text;
using System.Text.RegularExpressions;

namespace VolvoSplitter.Core.TriCore;

/// <summary>
/// Das VAG-Identifikationsfeld aus dem Dataset-Block: Teilenummern,
/// Softwarestand, Motorbezeichnung — die Angaben, die auf dem Steuergerät
/// stehen und die ein Diagnosetester meldet.
///
/// Anker ist die Systemkennung <c>EV_…</c>; alle Felder liegen bei festem
/// Versatz dazu und sind rechts mit Leerzeichen gefüllt:
///
///     -0x0C  12 B  Hardware-Teilenummer    „04L907309L"
///     +0x00  14 B  Systemkennung           „EV_ECM20TDI011"
///     +0x0E  13 B  Software-Teilenummer    „04L906027BB"
///     +0x1B   6 B  Index                   „005001"
///     +0x22  13 B  Software-Teilenummer    nochmals, gleicher Wert
///     +0x2F   4 B  Softwarestand           „7155"
///     +0x33  22 B  Freitext                meist leer, sonst z. B. „MED 17.1.62"
///     +0x49  21 B  Motorbezeichnung        „R4 2.0l TDI"
///     +0x5E  5×n   Motorkennbuchstaben     „DAZA ", „---- " = belegter Leerplatz
///
/// <strong>Herkunft.</strong> An fünf EDC17-/MED17-Abbildern vermessen (Audi A4,
/// VW Touran, Porsche Panamera, zwei weitere); in allen fünf steht das Feld im
/// Dataset-Block und alle Feldgrenzen stimmen überein. Die Werte zweier Abbilder
/// lassen sich unabhängig gegenprüfen — ihre Dateinamen nennen Teilenummer und
/// Softwarestand, und beides deckt sich mit dem, was hier gelesen wird.
///
/// Eine <em>feste Fundstelle</em>, kein Mustersuchen: Das ist derselbe Belegtyp
/// wie die Variantenkennung bei <c>+0x78</c> und deutlich belastbarer als das
/// freie Durchsuchen nach Zeichenketten in <see cref="BoschIdentity"/>.
/// </summary>
public sealed partial record VagIdentBlock(
    string HardwarePartNumber,
    string SystemName,
    string Index,
    string SoftwarePartNumber,
    string SoftwareLevel,
    string? EngineText,
    IReadOnlyList<string> EngineCodes,
    long Offset)
{
    /// <summary>Teilenummer und Stand zusammen, wie sie auf dem Gerät stehen.</summary>
    public string SoftwareText => $"{SoftwarePartNumber} {SoftwareLevel}";

    /// <summary>
    /// Form einer VAG-Teilenummer: drei Zeichen Fahrzeug-/Motorkennung, dann die
    /// Baugruppe, dann drei Ziffern, dann <em>null bis zwei</em> Buchstaben als
    /// Änderungsindex — <c>04L906027BB</c>, <c>03L906018B</c>, <c>7P0907401</c>.
    ///
    /// Die Baugruppe ist auf die vier Nummern eingegrenzt, unter denen
    /// Motorsteuergeräte laufen: 906 und 907 (Steuergerät), 910 und 997. Das ist
    /// die Regel, die eine Teilenummer von einer Bosch-Nummer trennt —
    /// <c>1037540589</c> und <c>0281020088</c> sind gleich lang, fallen hier aber
    /// durch.
    ///
    /// Der fehlende Änderungsindex ist kein Sonderfall, sondern kommt vor: die
    /// Hardware-Teilenummer <c>298907401</c> eines ausgewerteten Abbilds hat
    /// keinen.
    /// </summary>
    [GeneratedRegex(@"^[0-9A-Z]{3}(?:906|907|910|997)[0-9]{3}[A-Z]{0,2}$")]
    private static partial Regex PartNumberPattern();

    /// <summary>Trägt der Wert die Form einer VAG-Teilenummer?</summary>
    public static bool IsPartNumber(string value) => PartNumberPattern().IsMatch(value);

    /// <summary>Motorkennbuchstaben als Aufzählung, leer wenn keine hinterlegt sind.</summary>
    public string EngineCodeText => string.Join(", ", EngineCodes);

    // ==================================================================
    // Feldversätze, alle relativ zum EV_-Anker
    // ==================================================================

    public const int HardwareOffset = -12;
    public const int HardwareLength = 12;

    public const int SystemLength = 14;

    /// <summary>Erste Ablage der Software-Teilenummer — dient als Gegenprobe.</summary>
    public const int SoftwareFirstOffset = 14;

    public const int IndexOffset = 27;
    public const int IndexLength = 6;

    public const int SoftwareOffset = 34;
    public const int SoftwareLength = 13;

    public const int LevelOffset = 47;
    public const int LevelLength = 4;

    public const int FreeTextOffset = 51;
    public const int FreeTextLength = 22;

    public const int EngineOffset = 73;
    public const int EngineLength = 21;

    public const int EngineCodesOffset = 94;
    public const int EngineCodeStride = 5;
    public const int EngineCodeLength = 4;

    /// <summary>Deckel für die Kennbuchstabenliste — mehr wären unplausibel.</summary>
    public const int MaxEngineCodes = 32;

    /// <summary>Platzhalter für einen unbelegten Kennbuchstaben-Platz.</summary>
    public const string EmptyEngineCode = "----";

    /// <summary>Gesamtbreite des Felds ab dem Anker, ohne die Kennbuchstaben.</summary>
    private const int FixedEnd = EngineOffset + EngineLength;

    // ==================================================================

    /// <summary>
    /// Sucht das Feld und gibt es nur zurück, wenn <em>jede</em> Prüfung zutrifft.
    /// Ein halb passender Fund ist kein Fund — dann gibt es lieber keine Angabe
    /// als eine erfundene.
    /// </summary>
    public static VagIdentBlock? Find(ReadOnlySpan<byte> data)
    {
        for (int at = 0; at + FixedEnd <= data.Length; at++)
        {
            if (data[at] != (byte)'E' || data[at + 1] != (byte)'V' || data[at + 2] != (byte)'_')
                continue;

            if (at + HardwareOffset < 0) continue;

            if (TryRead(data, at) is { } found) return found;
        }

        return null;
    }

    /// <summary>
    /// Liest das Feld an einer bekannten Ankerstelle. Öffentlich, damit die
    /// Prüfregeln einzeln testbar sind.
    /// </summary>
    public static VagIdentBlock? TryRead(ReadOnlySpan<byte> data, long anchor)
    {
        if (anchor + HardwareOffset < 0 || anchor + FixedEnd > data.Length) return null;

        // 1. Systemkennung: „EV_" und danach nur Großbuchstaben, Ziffern,
        //    Unterstrich. Das ist die Form, die alle ausgewerteten Stände zeigen.
        if (Field(data, anchor, 0, SystemLength) is not { } system) return null;
        if (!system.StartsWith("EV_", StringComparison.Ordinal) || system.Length != SystemLength)
            return null;
        if (!system.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) return null;

        // 2. Softwarestand: genau vier Ziffern. Die schärfste Einzelprüfung —
        //    an ihr scheitert jeder Zufallstreffer.
        if (Field(data, anchor, LevelOffset, LevelLength) is not { } level) return null;
        if (level.Length != LevelLength || !level.All(char.IsAsciiDigit)) return null;

        // 3. Index: sechs Ziffern.
        if (Field(data, anchor, IndexOffset, IndexLength) is not { } index) return null;
        if (index.Length != IndexLength || !index.All(char.IsAsciiDigit)) return null;

        // 4. Software-Teilenummer in VAG-Form.
        if (Field(data, anchor, SoftwareOffset, SoftwareLength) is not { } software) return null;
        if (!IsPartNumber(software)) return null;

        // 5. Gegenprobe: dieselbe Nummer steht ein zweites Mal weiter vorn.
        //    Weichen die beiden ab, ist die Deutung falsch — nicht die Datei.
        if (Field(data, anchor, SoftwareFirstOffset, SoftwareLength) != software) return null;

        // 6. Hardware-Teilenummer, ebenfalls in VAG-Form.
        if (Field(data, anchor, HardwareOffset, HardwareLength) is not { } hardware) return null;
        if (!IsPartNumber(hardware)) return null;

        string? engine = Field(data, anchor, EngineOffset, EngineLength);
        if (engine is { Length: 0 }) engine = null;

        return new VagIdentBlock(hardware, system, index, software, level,
                                 engine, ReadEngineCodes(data, anchor), anchor);
    }

    /// <summary>
    /// Ein Feld fester Breite: druckbares ASCII, rechts beschnitten. Null, wenn
    /// ein Zeichen außerhalb liegt — dann ist es kein Textfeld und der ganze
    /// Fund fällt.
    /// </summary>
    private static string? Field(ReadOnlySpan<byte> data, long anchor, int offset, int length)
    {
        long at = anchor + offset;
        if (at < 0 || at + length > data.Length) return null;

        var text = new StringBuilder(length);
        for (int i = 0; i < length; i++)
        {
            byte b = data[(int)at + i];
            if (b < 0x20 || b > 0x7E) return null;
            text.Append((char)b);
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// Motorkennbuchstaben im 5-Zeichen-Raster. <c>----</c> ist ein belegter
    /// Leerplatz und wird verworfen; Wiederholungen ebenso, denn dieselbe
    /// Kennung steht über mehrere Varianten-Plätze hinweg mehrfach.
    ///
    /// Fehlt die Liste ganz — im Audi-Abbild stehen dort schlicht Binärdaten —,
    /// bleibt sie leer, ohne dass der übrige Fund dadurch ungültig wird.
    /// </summary>
    private static List<string> ReadEngineCodes(ReadOnlySpan<byte> data, long anchor)
    {
        var codes = new List<string>();

        for (int i = 0; i < MaxEngineCodes; i++)
        {
            int offset = EngineCodesOffset + i * EngineCodeStride;
            if (Field(data, anchor, offset, EngineCodeLength) is not { } code) break;

            if (code.Length != EngineCodeLength) break;
            if (code == EmptyEngineCode) continue;
            if (!code.All(char.IsAsciiLetterOrDigit)) break;

            if (!codes.Contains(code, StringComparer.Ordinal)) codes.Add(code);
        }

        return codes;
    }
}
