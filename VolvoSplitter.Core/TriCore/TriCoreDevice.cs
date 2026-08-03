namespace VolvoSplitter.Core.TriCore;

/// <summary>
/// Eine Flash-Bank des Bausteins mit ihrer Löschsektor-Einteilung.
/// </summary>
/// <param name="SectorSizes">
/// Die Löschsektoren der Bank, in Adressreihenfolge. Leer heißt: für diesen
/// Baustein ist keine Sektorkarte hinterlegt — <em>nicht</em>, dass er keine hat.
/// </param>
public sealed record FlashBank(string Label, long CpuStart, long Size,
                               IReadOnlyList<long> SectorSizes, bool EmulatedEeprom)
{
    public long CpuEnd => CpuStart + Size;
    public bool SectorMapKnown => SectorSizes.Count > 0;

    /// <summary>Summe der Löschsektoren — muss der Bankgröße entsprechen.</summary>
    public long SectorSum => SectorSizes.Sum();
}

/// <summary>
/// Ein Infineon-TriCore-Baustein mit seinen Flash-Bänken.
///
/// Ausgeliefert werden nur die beiden Bausteine, deren Datenblätter ausgewertet
/// wurden: <see cref="Tc1796"/> und <see cref="Tc1797"/>. Alle übrigen TriCore
/// der EDC17-/MED17-Familie werden <em>benannt</em>, tragen aber keine
/// Sektorkarte — eine geratene Karte wäre genau die Erfindung, die dieses
/// Werkzeug vermeidet.
///
/// Adressen (Infineon-Datenblätter):
///
///     PFLASH  gecacht 0x80000000 / ungecacht 0xA0000000
///     PMU1    (TC1797) 0x80800000 / 0xA0800000
///     DFLASH  0xAF000000
///     BROM    0xAFFFC000
///     EBU     externer Flash ab 0x84000000
///
/// Gecacht und ungecacht zeigen auf denselben Flash; siehe
/// <see cref="PhysicalLayout.SegmentMirror"/>.
/// </summary>
public sealed record TriCoreDevice(string Name, IReadOnlyList<FlashBank> Program,
                                   FlashBank? Data, long? ExternalBusBase, string SourceNote)
{
    public const long PflashBase = 0x80000000;
    public const long Pmu1Offset = 0x00800000;
    public const long DflashBase = 0xAF000000;
    public const long BromBase = 0xAFFFC000;

    /// <summary>Externer Flash über die EBU — bei MED17.1 nachweislich benutzt.</summary>
    public const long ExternalBase = 0x84000000;

    private const long Kib = 1024;
    private const long Bank = 2 * 1024 * Kib;   // 2 MiB je PMU

    /// <summary>
    /// Löschsektoren einer 2-MiB-Bank des TC1796:
    /// 8 × 16 KiB, 1 × 128 KiB, 1 × 256 KiB, 3 × 512 KiB — Summe exakt 2048 KiB.
    /// </summary>
    private static readonly long[] Tc1796Sectors =
    [
        16 * Kib, 16 * Kib, 16 * Kib, 16 * Kib, 16 * Kib, 16 * Kib, 16 * Kib, 16 * Kib,
        128 * Kib,
        256 * Kib,
        512 * Kib, 512 * Kib, 512 * Kib
    ];

    /// <summary>
    /// Löschsektoren einer 2-MiB-Bank des TC1797:
    /// 8 × 16 KiB, 1 × 128 KiB, 7 × 256 KiB — Summe exakt 2048 KiB.
    /// </summary>
    private static readonly long[] Tc1797Sectors =
    [
        16 * Kib, 16 * Kib, 16 * Kib, 16 * Kib, 16 * Kib, 16 * Kib, 16 * Kib, 16 * Kib,
        128 * Kib,
        256 * Kib, 256 * Kib, 256 * Kib, 256 * Kib, 256 * Kib, 256 * Kib, 256 * Kib
    ];

    /// <summary>TC1796: ein PMU mit 2 MiB PFLASH, 128 KiB DFLASH in zwei Bänken.</summary>
    public static readonly TriCoreDevice Tc1796 = new(
        "TC1796",
        [new FlashBank("PFLASH", PflashBase, Bank, Tc1796Sectors, EmulatedEeprom: false)],
        new FlashBank("DFLASH", DflashBase, 128 * Kib, [64 * Kib, 64 * Kib], EmulatedEeprom: true),
        ExternalBase,
        "TC1796, Infineon-Datenblatt — 2 MiB PFLASH, 128 KiB DFLASH");

    /// <summary>
    /// TC1797: PMU0 und PMU1 zu je 2 MiB, 64 KiB DFLASH in zwei Bänken
    /// (16 KiB davon EEPROM-Emulation), beides nur an PMU0.
    ///
    /// Das Datenblatt nennt „4 oder 3 MiB — derivative dependent". Für die
    /// 3-MiB-Ausführung gibt es <em>keine</em> Sektoraufteilung der dann 1 MiB
    /// großen PMU1; dieser Fall wird benannt, aber nicht gefüllt.
    /// </summary>
    public static readonly TriCoreDevice Tc1797 = new(
        "TC1797",
        [
            new FlashBank("PMU0", PflashBase, Bank, Tc1797Sectors, EmulatedEeprom: false),
            new FlashBank("PMU1", PflashBase + Pmu1Offset, Bank, Tc1797Sectors, EmulatedEeprom: false)
        ],
        new FlashBank("DFLASH", DflashBase, 64 * Kib, [32 * Kib, 32 * Kib], EmulatedEeprom: true),
        ExternalBase,
        "TC1797, Infineon-Datenblatt — PMU0 + PMU1 je 2 MiB, 64 KiB DFLASH");

    public static IReadOnlyList<TriCoreDevice> Mapped => [Tc1796, Tc1797];

    /// <summary>
    /// TriCore der EDC17-/MED17-Familie, deren Datenblatt nicht ausgewertet
    /// wurde. Sie werden erkannt und benannt, bekommen aber keine Sektorkarte.
    /// </summary>
    public static IReadOnlyList<string> NamedWithoutSectorMap =>
        ["TC1762", "TC1766", "TC1767", "TC1782", "TC1791", "TC1793"];

    /// <summary>Für alle Bänke ist eine Löschsektor-Einteilung hinterlegt.</summary>
    public bool SectorMapKnown => Program.Count > 0 && Program.All(b => b.SectorMapKnown);

    public long ProgramSize => Program.Sum(b => b.Size);

    /// <summary>
    /// Baustein ohne hinterlegte Sektorkarte. Das Layout besteht dann aus einer
    /// einzigen PFLASH-Partition; <see cref="PhysicalLayout.Complete"/> ist false,
    /// und <see cref="PhysicalLayout.EraseSectors"/> bleibt leer.
    /// </summary>
    public static TriCoreDevice Generic(long imageSize) =>
        new("TriCore (Baustein nicht bestimmt)", [], null, ExternalBase,
            "Sektoreinteilung für diesen Baustein nicht hinterlegt");

    /// <summary>
    /// Baustein zu einem Namen, wie ihn die Protokolldatei des Auslesegeräts
    /// im Feld <c>Micro</c> nennt. Null, wenn der Name keinen TriCore benennt.
    /// </summary>
    public static TriCoreDevice? ByName(string? micro)
    {
        if (string.IsNullOrWhiteSpace(micro)) return null;

        string text = micro.ToUpperInvariant();

        if (text.Contains("TC1796")) return Tc1796;
        if (text.Contains("TC1797")) return Tc1797;

        foreach (string name in NamedWithoutSectorMap)
            if (text.Contains(name)) return Unmapped(name);

        return null;
    }

    /// <summary>Benannter Baustein ohne Sektorkarte.</summary>
    public static TriCoreDevice Unmapped(string name) =>
        new(name, [], null, ExternalBase,
            $"{name} — Sektoreinteilung nicht hinterlegt, Datenblatt nicht ausgewertet");
}
