namespace VolvoSplitter.Core;

public enum EcuFamily
{
    Ems23,
    Ems24
}

public enum SectorKind
{
    Parameter,
    Asw,
    Calibration,
    Other
}

/// <summary>Ein Sektorplatz im Flash-Abbild.</summary>
public sealed record SectorSlot(SectorKind Kind, string Label, string Prefix, long Start, long CpuOffset)
{
    /// <summary>
    /// Adresse, an der dieser Sektor laut Tabelle stehen sollte. Nur gesetzt,
    /// wenn er woanders gefunden wurde.
    /// </summary>
    public long? ExpectedStart { get; init; }
}

/// <summary>
/// Aufbau der TRW-EMS2.3/EMS2.4-Flash-Abbilder.
///
///     +0x000  ASCII-Kopf   "v=1;a=&lt;TeileNr&gt;;p=...;o=&lt;CPU-Adresse&gt;;"
///     +0x006  Teilenummer  (11 Zeichen)              -> Dateiname
///     +0x0F8  Endadresse   (4 Byte, big endian, CPU-Adressraum)
///     +0x0F8  ... Nutzdaten, über die die CRC32 läuft
///     Ende-4  CRC32        (4 Byte, big endian)
///
///     Länge = Endadresse - CPU-Offset + 44
///
/// Das Abbild ist das rohe Flash ab 0x000000; im CPU-Adressraum liegt es ab 0x800000.
/// Bei EMS2.4 können hinter diesen 8 MiB weitere physische Blöcke des MPC5777C
/// angehängt sein — siehe <see cref="Mpc5777cLayout"/>.
/// </summary>
public static class FlashFormat
{
    public const int VersionStringLen = 11;
    public const int VersionPosOffset = 6;
    public const int EndAddrOffset = 248;   // 0x0F8
    public const int CrcOffset = 44;        // 0x02C
    public const int CrcTrailerLen = 4;

    public const long Ems23MaxSize = 0x500000;   // darüber: EMS2.4

    /// <summary>Kleinster sinnvoller Sektor: Kopf + Endadresse + CRC.</summary>
    public const int MinSectorLength = EndAddrOffset + CrcTrailerLen;

    /// <summary>
    /// Länge eines Sektors laut Format: von der Endadresse und dem CPU-Offset
    /// aus seinem Kopf. Zentral, damit die Formel nicht an mehreren Stellen
    /// getrennt gepflegt werden muss.
    /// </summary>
    public static long SectorLength(long endAddr, long cpuOffset) =>
        endAddr - cpuOffset + CrcOffset;

    public static readonly byte[] HeaderMagic = "v=1;a="u8.ToArray();

    // --- Fahrzeugdaten im Parameter-Sektor (in beiden Beispieldumps identisch) ---
    public const int ChassisOffset = 0x100;
    public const int ChassisLength = 11;
    public const int MakerOffset = 0x10B;
    public const int MakerLength = 5;      // "VOLVO"
    public const int VinOffset = 0x120;
    public const int VinLength = 17;

    private static readonly SectorSlot[] Ems23Slots =
    [
        new(SectorKind.Parameter,   "Parameter",    "param_", 0x060000, 0x060000),
        new(SectorKind.Asw,         "ASW",          "asw_",   0x100000, 0x100000),
        new(SectorKind.Calibration, "Kalibrierung", "cal_",   0x380000, 0x380000)
    ];

    private static readonly SectorSlot[] Ems24Slots =
    [
        new(SectorKind.Asw,         "ASW",          "asw_",   0x200000, 0xA00000),
        new(SectorKind.Calibration, "Kalibrierung", "cal_",   0x740000, 0xF40000),
        new(SectorKind.Parameter,   "Parameter",    "param_", 0x7C0000, 0xFC0000)
    ];

    public static EcuFamily DetectFamily(long imageSize) =>
        imageSize <= Ems23MaxSize ? EcuFamily.Ems23 : EcuFamily.Ems24;

    public static IReadOnlyList<SectorSlot> SlotsFor(EcuFamily family) =>
        family == EcuFamily.Ems23 ? Ems23Slots : Ems24Slots;

    /// <summary>
    /// Größe des Flash-Bereichs, den die Sektortabelle beschreibt — beim
    /// MPC5777C der 8 MiB große Large Flash.
    ///
    /// Was im Abbild dahinter steht, ist <em>nicht</em> pauschal EEPROM: beim
    /// MPC5777C folgen vier gewöhnliche 64-KiB-Flash-Blöcke und der UTEST-Bereich.
    /// Welcher davon was enthält, sagt <see cref="Mpc5777cLayout"/>.
    /// </summary>
    public static long FlashSizeFor(EcuFamily family) =>
        family == EcuFamily.Ems23 ? 0x400000 : Mpc5777cLayout.LargeFlashSize;

    public static string FamilyName(EcuFamily family) =>
        family == EcuFamily.Ems23 ? "EMS2.3" : "EMS2.4";

    /// <summary>Erwarteter Mikrocontroller — nur zur Anzeige.</summary>
    public static string MicroName(EcuFamily family) =>
        family == EcuFamily.Ems23 ? "MPC5674F" : "MPC5777C";
}
