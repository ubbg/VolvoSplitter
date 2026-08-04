using VolvoSplitter.Core.TriCore;

namespace VolvoSplitter.Core;

/// <summary>Containerformat, in dem die Datensätze eines Abbilds liegen.</summary>
public enum ContainerKind
{
    /// <summary>TRW-Sektoren mit ASCII-Kopf „v=1;a=" und CRC32-Trailer.</summary>
    TrwSector,

    /// <summary>Verkettete Bosch-Blöcke mit Kopf, Prüfsummenstrukturen und 0xDEADBEEF.</summary>
    BoschBlockChain,

    /// <summary>Kein bekanntes Containerformat — nur Bereiche.</summary>
    None
}

/// <summary>
/// Was über das vorliegende Abbild bekannt ist: Gerätefamilie, Baustein,
/// Bytereihenfolge und Containerformat. Ersetzt die vier Ternärausdrücke, mit
/// denen <see cref="FlashFormat"/> bis Version 1 zwischen EMS2.3 und EMS2.4
/// unterschied.
/// </summary>
/// <param name="SupportsWriteBack">
/// Ob das Werkzeug für dieses Profil schreiben darf. Für TriCore-Abbilder
/// ausdrücklich false: Prüfsummen werden gerechnet und gemeldet, nicht gestellt.
/// Die Festlegung steht damit als Feld im Code und nicht bloß in weggelassenen
/// Codepfaden.
/// </param>
/// <param name="SupportsBlockTransfer">
/// Ob ein Block 1:1 aus einem anderen Abbild an <em>dieselbe</em> CPU-Adresse
/// übernommen werden darf.
///
/// Ein anderer Vorgang als <paramref name="SupportsWriteBack"/> und deshalb ein
/// eigenes Feld: dort werden Werte <em>gerechnet und gestellt</em>, hier werden
/// vorhandene Bytes übernommen. Für TriCore gilt weiterhin, dass keine
/// Prüfsumme gestellt wird — der Übertrag rührt keine an, er kopiert nur und
/// rechnet danach nach.
///
/// Bei <c>unknown</c> false: ohne erkannte Blockstruktur gibt es keinen Block,
/// sondern nur Bytes an einem Offset. Eine Kopie dorthin wäre genau das Raten,
/// das dieses Werkzeug nicht tut.
/// </param>
public sealed record EcuProfile(
    string Key,
    string FamilyName,
    string MicroName,
    string Manufacturer,
    Endianness Endianness,
    long FlashSize,
    ContainerKind Container,
    bool SupportsWriteBack,
    bool SupportsBlockTransfer)
{
    /// <summary>
    /// Es gibt überhaupt einen Vorgang, der dieses Abbild verändert — also auch
    /// etwas zu speichern. Ein Übertrag, dessen Ergebnis sich nicht sichern
    /// ließe, wäre nutzlos.
    /// </summary>
    public bool SupportsSaving => SupportsWriteBack || SupportsBlockTransfer;

    /// <summary>Nur bei den beiden TRW-Familien gesetzt.</summary>
    public EcuFamily? Family { get; init; }

    /// <summary>Nur bei TriCore-Abbildern gesetzt.</summary>
    public TriCoreDevice? Device { get; init; }

    /// <summary>
    /// CPU-Adresse des Datei-Offsets 0, sofern sie an den Blockköpfen gemessen
    /// wurde; null heißt „Anfang des Bausteins". Sie steht hier und nicht nur im
    /// Erkennungsergebnis, weil <see cref="EcuProfiles.LayoutFor"/> das Layout
    /// später ein zweites Mal baut — ohne sie liefe der Bericht auf einem
    /// anderen Nullpunkt als die gelesene Blockkette.
    /// </summary>
    public long? WindowStart { get; init; }

    /// <summary>Sektortabelle des TRW-Formats; bei TriCore leer.</summary>
    public IReadOnlyList<SectorSlot> Slots { get; init; } = [];

    public string Headline => MicroName.Length > 0 && MicroName != "—"
        ? $"{FamilyName} · {MicroName}"
        : FamilyName;
}

/// <summary>Die bekannten Profile. Fest im Code, wie die Sektortabellen auch.</summary>
public static class EcuProfiles
{
    internal static readonly SectorSlot[] Ems23Slots =
    [
        new(SectorKind.Parameter,   "Parameter",    "param_", 0x060000, 0x060000),
        new(SectorKind.Asw,         "ASW",          "asw_",   0x100000, 0x100000),
        new(SectorKind.Calibration, "Kalibrierung", "cal_",   0x380000, 0x380000)
    ];

    internal static readonly SectorSlot[] Ems24Slots =
    [
        new(SectorKind.Asw,         "ASW",          "asw_",   0x200000, 0xA00000),
        new(SectorKind.Calibration, "Kalibrierung", "cal_",   0x740000, 0xF40000),
        new(SectorKind.Parameter,   "Parameter",    "param_", 0x7C0000, 0xFC0000)
    ];

    public static readonly EcuProfile Ems23 = new(
        "ems23", "EMS2.3", "MPC5674F", "Volvo / TRW",
        Endianness.Big, 0x400000, ContainerKind.TrwSector,
        SupportsWriteBack: true, SupportsBlockTransfer: true)
    {
        Family = EcuFamily.Ems23,
        Slots = Ems23Slots
    };

    public static readonly EcuProfile Ems24 = new(
        "ems24", "EMS2.4", "MPC5777C", "Volvo / TRW",
        Endianness.Big, Mpc5777cLayout.LargeFlashSize, ContainerKind.TrwSector,
        SupportsWriteBack: true, SupportsBlockTransfer: true)
    {
        Family = EcuFamily.Ems24,
        Slots = Ems24Slots
    };

    public static EcuProfile ForFamily(EcuFamily family) =>
        family == EcuFamily.Ems23 ? Ems23 : Ems24;

    /// <summary>
    /// Profil für ein TriCore-Abbild. Der Flash-Bereich ist die Datei selbst —
    /// anders als beim TRW-Format gibt es keine Sektortabelle, die einen
    /// kleineren Ausschnitt beschreibt.
    /// </summary>
    public static EcuProfile ForTriCore(TriCoreDevice device, long imageSize,
                                        string? familyName = null, long? windowStart = null) =>
        new("tricore", familyName ?? "EDC17 / MED17 (TriCore)", device.Name, "VAG / Bosch",
            Endianness.Little, imageSize, ContainerKind.BoschBlockChain,
            // Nur lesen bleibt nur lesen: keine Prüfsumme wird gestellt. Der
            // Blockübertrag 1:1 ist davon unberührt — er übernimmt Bytes, die
            // ein anderes Abbild schon trägt, und rechnet danach nach.
            SupportsWriteBack: false, SupportsBlockTransfer: true)
        {
            Device = device,
            WindowStart = windowStart
        };

    /// <summary>
    /// Kein Profil erkannt. Kein Container, kein Layout — der Bereichsscanner
    /// liefert dann genau das, was sich ohne Formatkenntnis sagen lässt.
    /// </summary>
    public static EcuProfile Unknown(long imageSize) =>
        new("unknown", "unbekannt", "—", "unbekannt",
            Endianness.Big, imageSize, ContainerKind.None,
            SupportsWriteBack: false, SupportsBlockTransfer: false);

    /// <summary>Profile, die sich über <c>--profile</c> erzwingen lassen.</summary>
    public static IReadOnlyList<string> Keys =>
        ["ems23", "ems24", "tricore", "tc1796", "tc1797", "unknown"];

    /// <summary>Profil zu einem Namen von der Kommandozeile, oder null.</summary>
    public static EcuProfile? ByKey(string key, long imageSize) => key.ToLowerInvariant() switch
    {
        "ems23" => Ems23,
        "ems24" => Ems24,
        "tricore" => ForTriCore(TriCoreDevice.Generic(imageSize), imageSize),
        "tc1796" => ForTriCore(TriCoreDevice.Tc1796, imageSize),
        "tc1797" => ForTriCore(TriCoreDevice.Tc1797, imageSize),
        "unknown" => Unknown(imageSize),
        _ => null
    };

    /// <summary>Das physische Layout zu einem Profil, falls eines hinterlegt ist.</summary>
    public static PhysicalLayout? LayoutFor(EcuProfile profile, long imageSize)
    {
        if (profile.Device is { } device)
            return TriCoreLayout.For(device, imageSize, profile.WindowStart);
        if (profile.Family is { } family) return Mpc5777cLayout.For(family, imageSize);
        return null;
    }
}
