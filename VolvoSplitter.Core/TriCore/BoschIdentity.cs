using System.Text;
using System.Text.RegularExpressions;

namespace VolvoSplitter.Core.TriCore;

/// <summary>Eine Kennung samt Fundort. Der Fundort ist der Beleg, der Wert allein nur eine Beobachtung.</summary>
public sealed record IdentityHit(string Kind, string Value, long Offset)
{
    public RegionConfidence Confidence { get; init; } = RegionConfidence.Strong;

    public string ConfidenceText => Confidence switch
    {
        RegionConfidence.Confirmed => "gesichert",
        RegionConfidence.Strong => "stark gestützt",
        _ => "unbekannt"
    };

    public string Display => $"{Kind,-16} {Value,-24} bei {Hex.Addr(Offset)}  [{ConfidenceText}]";
}

/// <summary>
/// Liest die Kennungen eines Bosch-Abbilds. <strong>Ein</strong> linearer
/// Durchlauf sammelt druckbare ASCII-Läufe samt Offset; die Muster laufen
/// anschließend über die Läufe, nicht über die Rohbytes. Billiger — und der
/// Fundort fällt gratis ab.
///
/// Vorrang hat die feste Fundstelle: steht im Dataset-Block bei +0x78 eine
/// Variantenkennung, wird die Steuergerätevariante von dort genommen und gilt
/// als gesichert. Das freie Durchsuchen ist nur der Rückfall für Abbilder ohne
/// lesbaren Dataset-Block und liefert nie mehr als „stark gestützt".
///
/// Die Formen sind streng gefasst. Lockeres Matchen hieße behaupten.
/// </summary>
public sealed partial record BoschIdentity(IReadOnlyList<IdentityHit> Hits)
{
    /// <summary>Der Steuergerätetyp, wenn einer gefunden wurde — bevorzugt der gesicherte.</summary>
    public string? EcuType => Hits.Where(h => h.Kind == "Steuergerät")
                                  .OrderBy(h => h.Confidence)
                                  .FirstOrDefault()?.Value;

    public string? VehicleNumber => Hits.FirstOrDefault(h => h.Kind == "VIN")?.Value;

    /// <summary>
    /// Das VAG-Identifikationsfeld aus dem Dataset-Block, falls vorhanden —
    /// Teilenummern, Softwarestand, Motor. Null heißt: nicht gefunden, nicht
    /// „keins vorhanden".
    /// </summary>
    public VagIdentBlock? Vag { get; init; }

    /// <summary>Kürzeste ASCII-Folge, die überhaupt betrachtet wird.</summary>
    private const int MinRunLength = 6;

    /// <summary>Abstand, innerhalb dessen eine Teilenummer neben einer Softwarenummer stehen muss.</summary>
    private const long PartNumberProximity = 0x400;

    /// <summary>Weltherstellercodes des VAG-Konzerns.</summary>
    private static readonly string[] VinPrefixes =
        ["WVW", "WAU", "TMB", "VSS", "TRU", "WV1", "WV2"];

    [GeneratedRegex(@"(?<!\d)0(?:281|261)\d{6}(?!\d)")]
    private static partial Regex HardwareNumber();

    [GeneratedRegex(@"(?<!\d)10(?:37|39)\d{6}(?!\d)")]
    private static partial Regex SoftwareNumber();

    [GeneratedRegex(@"(?<![0-9A-Z])\d{2}SW\d{6}(?![0-9])")]
    private static partial Regex BlockIdentifier();

    [GeneratedRegex(@"EDC17[ _]?[A-Z]{0,2}\d{2}")]
    private static partial Regex DieselEcu();

    [GeneratedRegex(@"(?<![A-Z])M(?:ED)?C?17(?:\.\d+)*(?![0-9])")]
    private static partial Regex PetrolEcu();

    /// <summary>
    /// Suchform von <see cref="VagIdentBlock.IsPartNumber"/>: dieselbe Regel,
    /// nur mit Wortgrenzen statt Anker, damit sie in einem ASCII-Lauf greift.
    /// </summary>
    [GeneratedRegex(@"(?<![0-9A-Z])[0-9A-Z]{3}(?:906|907|910|997)[0-9]{3}[A-Z]{0,2}(?![0-9A-Z])")]
    private static partial Regex PartNumber();

    [GeneratedRegex(@"(?<![A-HJ-NPR-Z0-9])[A-HJ-NPR-Z0-9]{17}(?![A-HJ-NPR-Z0-9])")]
    private static partial Regex VinPattern();

    /// <summary>
    /// Sucht alle Kennungen. <paramref name="variant"/> ist die Zeichenkette aus
    /// dem Dataset-Block; ist sie gesetzt, gilt der daraus gelesene
    /// Steuergerätetyp als gesichert.
    /// </summary>
    public static BoschIdentity? Scan(ReadOnlySpan<byte> data,
                                      string? variant = null, long variantOffset = 0)
    {
        var runs = CollectRuns(data);
        var hits = new List<IdentityHit>();

        // Feste Fundstelle zuerst: das VAG-Identifikationsfeld ist eine Struktur
        // mit geprüften Feldgrenzen und gilt deshalb als gesichert.
        var vag = VagIdentBlock.Find(data);
        if (vag is not null) CollectVag(hits, vag);

        if (variant is not null)
        {
            hits.Add(new IdentityHit("Variante", variant, variantOffset)
            {
                Confidence = RegionConfidence.Confirmed
            });

            if (EcuTypeFromVariant(variant) is { } type)
                hits.Add(new IdentityHit("Steuergerät", type, variantOffset)
                {
                    Confidence = RegionConfidence.Confirmed
                });
        }

        foreach (var (text, offset) in runs)
        {
            Collect(hits, "Hardware", HardwareNumber(), text, offset);
            Collect(hits, "Software", SoftwareNumber(), text, offset);
            Collect(hits, "Blockkennung", BlockIdentifier(), text, offset);
            Collect(hits, "Steuergerät", DieselEcu(), text, offset);
            Collect(hits, "Steuergerät", PetrolEcu(), text, offset);
            CollectVin(hits, text, offset);
        }

        CollectPartNumbers(hits, runs, vag);

        var unique = Deduplicate(hits);
        return unique.Count > 0 ? new BoschIdentity(unique) { Vag = vag } : null;
    }

    /// <summary>
    /// Macht aus dem Identifikationsfeld Kennungen. Alle gelten als gesichert:
    /// sie stammen aus einer Struktur, deren Feldgrenzen geprüft sind, nicht aus
    /// einem Mustertreffer irgendwo im Abbild.
    /// </summary>
    private static void CollectVag(List<IdentityHit> into, VagIdentBlock vag)
    {
        void Add(string kind, string? value, int offset)
        {
            if (string.IsNullOrEmpty(value)) return;
            into.Add(new IdentityHit(kind, value, vag.Offset + offset)
            {
                Confidence = RegionConfidence.Confirmed
            });
        }

        Add("VAG-Software", vag.SoftwarePartNumber, VagIdentBlock.SoftwareOffset);
        Add("Softwarestand", vag.SoftwareLevel, VagIdentBlock.LevelOffset);
        Add("VAG-Hardware", vag.HardwarePartNumber, VagIdentBlock.HardwareOffset);
        Add("Systemkennung", vag.SystemName, 0);
        Add("Motor", vag.EngineText, VagIdentBlock.EngineOffset);

        if (vag.EngineCodes.Count > 0)
            Add("Motorkennung", vag.EngineCodeText, VagIdentBlock.EngineCodesOffset);
    }

    /// <summary>
    /// Aus <c>34/1/EDC17_C46/5/P643//C643X5L8///</c> das Feld, das die Variante
    /// benennt. Feste Fundstelle, feste Trennzeichen — kein Raten.
    /// </summary>
    public static string? EcuTypeFromVariant(string variant)
    {
        foreach (string field in variant.Split('/'))
        {
            string candidate = field.Trim();
            if (candidate.Length == 0) continue;

            if (DieselEcu().IsMatch(candidate) || PetrolEcu().IsMatch(candidate))
                return candidate;
        }
        return null;
    }

    // ------------------------------------------------------------------

    private static List<(string Text, long Offset)> CollectRuns(ReadOnlySpan<byte> data)
    {
        var runs = new List<(string, long)>();
        var current = new StringBuilder();
        long start = 0;

        for (long i = 0; i <= data.Length; i++)
        {
            byte b = i < data.Length ? data[(int)i] : (byte)0;

            if (i < data.Length && b >= 0x20 && b <= 0x7E)
            {
                if (current.Length == 0) start = i;
                current.Append((char)b);
                continue;
            }

            if (current.Length >= MinRunLength) runs.Add((current.ToString(), start));
            current.Clear();
        }

        return runs;
    }

    private static void Collect(List<IdentityHit> into, string kind, Regex pattern,
                                string text, long offset)
    {
        foreach (Match match in pattern.Matches(text))
            into.Add(new IdentityHit(kind, match.Value, offset + match.Index));
    }

    private static void CollectVin(List<IdentityHit> into, string text, long offset)
    {
        foreach (Match match in VinPattern().Matches(text))
            if (VinPrefixes.Contains(match.Value[..3]))
                into.Add(new IdentityHit("VIN", match.Value, offset + match.Index));
    }

    /// <summary>
    /// Der Rückfall für Abbilder ohne lesbares Identifikationsfeld. Eine
    /// Teilenummer gilt nur, wenn zwei Bedingungen zusammenkommen: die strenge
    /// Form <em>und</em> Nähe zu einer Software- oder Hardwarenummer. Elf
    /// Zeichen allein sind keine VAG-Teilenummer.
    ///
    /// Nummern, die <paramref name="vag"/> schon gesichert geliefert hat, werden
    /// übersprungen — sonst stünde dieselbe Nummer zweimal im Bericht, einmal
    /// „gesichert" und einmal „stark gestützt".
    /// </summary>
    private static void CollectPartNumbers(List<IdentityHit> into,
                                           List<(string Text, long Offset)> runs,
                                           VagIdentBlock? vag)
    {
        var anchors = into.Where(h => h.Kind is "Software" or "Hardware")
                          .Select(h => h.Offset)
                          .ToList();
        if (anchors.Count == 0) return;

        foreach (var (text, offset) in runs)
            foreach (Match match in PartNumber().Matches(text))
            {
                if (vag is not null &&
                    (match.Value == vag.SoftwarePartNumber || match.Value == vag.HardwarePartNumber))
                    continue;

                long at = offset + match.Index;
                if (!anchors.Any(a => Math.Abs(a - at) <= PartNumberProximity)) continue;

                into.Add(new IdentityHit("Teilenummer", match.Value, at));
            }
    }

    private static List<IdentityHit> Deduplicate(List<IdentityHit> hits) =>
        hits.GroupBy(h => (h.Kind, h.Value))
            .Select(g => g.OrderBy(h => h.Confidence).ThenBy(h => h.Offset).First())
            .OrderBy(h => h.Kind)
            .ThenBy(h => h.Offset)
            .ToList();
}
