namespace VolvoSplitter.Core;

/// <summary>Blocktyp des MPC5777C-Flash laut Referenzhandbuch, Tabelle 4-2.</summary>
public enum FlashBlockType
{
    /// <summary>8 MiB Large Flash, 32 Sektoren zu 256 KiB, CPU 0x800000–0xFFFFFF.</summary>
    Large,

    /// <summary>64-KiB-Low-Block, CPU 0x000000–0x01FFFF.</summary>
    Low,

    /// <summary>64-KiB-Mid-Block, CPU 0x020000–0x03FFFF.</summary>
    Mid,

    /// <summary>16-KiB-UTEST-Block, CPU 0x400000–0x403FFF.</summary>
    Utest,

    /// <summary>Im Abbild vorhanden, aber keiner Adresse zuzuordnen.</summary>
    Unknown
}

/// <summary>
/// Ein physischer Flash-Block, wie er im .MPC-Container liegt: Datei-Offset und
/// — soweit rekonstruierbar — die zugehörige CPU-Adresse.
/// </summary>
/// <param name="CpuStart">Null, wenn der Bereich keiner CPU-Adresse zuzuordnen ist.</param>
public sealed record FlashPartition(string Label, long FileStart, long FileLength, long? CpuStart,
                                    FlashBlockType Type, int? RwwPartition, string ExampleUse)
{
    public long FileEnd => FileStart + FileLength;

    public bool Contains(long fileOffset) => fileOffset >= FileStart && fileOffset < FileEnd;

    /// <summary>CPU-Adresse zu einem Datei-Offset in diesem Block.</summary>
    public long? ToCpu(long fileOffset) =>
        CpuStart is { } cpu && Contains(fileOffset) ? cpu + (fileOffset - FileStart) : null;

    public string FileRange => $"0x{FileStart:X6} – 0x{FileEnd:X6}";

    public string? CpuRange => CpuStart is { } cpu ? $"0x{cpu:X6} – 0x{cpu + FileLength:X6}" : null;
}

/// <summary>Zustand eines physischen Blocks im vorliegenden Abbild.</summary>
public enum PartitionState
{
    /// <summary>Enthält Daten.</summary>
    Occupied,

    /// <summary>Vollständig 0xFF — im Baustein tatsächlich gelöscht.</summary>
    Erased,

    /// <summary>Vollständig 0xFF, obwohl der Block das gar nicht sein kann.</summary>
    NotRead
}

/// <summary>Ein physischer Block zusammen mit dem, was im Abbild darin steht.</summary>
public sealed record PartitionInfo(FlashPartition Partition, PartitionState State, string Description)
{
    public string Label => Partition.Label;
    public string FileRange => Partition.FileRange;
    public string CpuRange => Partition.CpuRange ?? "nicht zuzuordnen";
    public string SizeText => $"{Partition.FileLength:N0} B";

    public string Detail => Partition.RwwPartition is { } rww
        ? $"{Partition.ExampleUse} · RWW-Partition {rww}"
        : Partition.ExampleUse;

    public string StateText => State switch
    {
        PartitionState.Erased => "gelöscht",
        PartitionState.NotRead => "nicht ausgelesen",
        _ => "belegt"
    };
}

/// <summary>
/// Physisches Speicherlayout des MPC5777C, wie es im .MPC-Container abgelegt ist.
///
/// Das Auslesegerät hängt drei getrennte Adressräume hintereinander in eine Datei:
///
///     Datei 0x000000–0x7FFFFF  ->  CPU 0x800000–0xFFFFFF   8 MiB Large Flash
///     Datei 0x800000–0x83FFFF  ->  CPU 0x000000–0x03FFFF   4 × 64 KiB Low/Mid
///     Datei 0x840000–0x843FFF  ->  CPU 0x400000–0x403FFF   16 KiB UTEST
///
/// Die Summe ergibt genau <see cref="ContainerSize"/> = 0x844000; es bleibt kein Byte
/// unerklärt. Die beiden 16-KiB-High-Blöcke (CSE, CPU 0x600000–0x607FFF) sind nicht
/// enthalten — mit ihnen wäre die Datei 0x84C000 groß.
///
/// Quelle: NXP MPC5777C Reference Manual, Kapitel 4, Tabelle 4-2 und 4-3
/// (im Repository als „MPC5777CRM, MPC5777C Reference Manual.pdf").
///
/// Die Zuordnung Container -> CPU ist eine Interpretation: NXP dokumentiert das
/// proprietäre Dateiformat nicht. Sie wird durch die Dateigröße, die Sektorköpfe
/// (0x740000 nennt selbst 0xF40000) und die Adressfelder im VOLVOECU-Block gestützt.
/// </summary>
public sealed class Mpc5777cLayout
{
    /// <summary>8 MiB Large Flash — der Teil, den die Sektortabelle beschreibt.</summary>
    public const long LargeFlashSize = 0x800000;

    /// <summary>Vier Low/Mid-Blöcke zu je 64 KiB.</summary>
    public const long LowMidBlockSize = 0x10000;
    public const long LowMidSize = 4 * LowMidBlockSize;

    /// <summary>UTEST-Block, 16 KiB.</summary>
    public const long UtestSize = 0x4000;

    /// <summary>Größe eines vollständigen MPC5777C-Containers.</summary>
    public const long ContainerSize = LargeFlashSize + LowMidSize + UtestSize;   // 0x844000

    private Mpc5777cLayout(IReadOnlyList<FlashPartition> partitions, bool complete)
    {
        Partitions = partitions;
        Complete = complete;
    }

    public IReadOnlyList<FlashPartition> Partitions { get; }

    /// <summary>
    /// Die Containerabbildung ging vollständig auf. Sonst ist nur der Large Flash
    /// sicher, der Rest bleibt unzugeordnet.
    /// </summary>
    public bool Complete { get; }

    /// <summary>
    /// Layout für ein Abbild, oder null, wenn es nicht passt. Bewusst streng:
    /// nur EMS2.4 (MPC5777C) und nur ab der Größe des Large Flash.
    /// </summary>
    public static Mpc5777cLayout? For(EcuFamily family, long imageSize)
    {
        if (family != EcuFamily.Ems24 || imageSize < LargeFlashSize) return null;

        var partitions = new List<FlashPartition>
        {
            new("Large Flash", 0, LargeFlashSize, LargeFlashSize, FlashBlockType.Large,
                null, "Boot, Kalibrierung, Anwendungscode — 32 Sektoren zu 256 KiB")
        };

        if (imageSize == LargeFlashSize)
            return new Mpc5777cLayout(partitions, complete: true);

        if (imageSize != ContainerSize)
        {
            // Größe passt zu keinem bekannten Container: den Anhang zeigen, aber
            // keine CPU-Adresse behaupten.
            partitions.Add(new FlashPartition("Anhang", LargeFlashSize, imageSize - LargeFlashSize,
                null, FlashBlockType.Unknown, null,
                "Größe passt zu keinem bekannten Containerlayout"));
            return new Mpc5777cLayout(partitions, complete: false);
        }

        // Vier 64-KiB-Blöcke: zwei Low, zwei Mid. Blocknummer zählt je Typ neu,
        // die RWW-Partition durchgehend 0..3 (RM Tabelle 4-2).
        for (int i = 0; i < 4; i++)
        {
            bool low = i < 2;
            partitions.Add(new FlashPartition(
                $"{(low ? "Low" : "Mid")}-Block {i % 2}",
                LargeFlashSize + i * LowMidBlockSize, LowMidBlockSize, i * LowMidBlockSize,
                low ? FlashBlockType.Low : FlashBlockType.Mid, i,
                "NXP-Beispielnutzung: EEPROM-Daten"));
        }

        partitions.Add(new FlashPartition("UTEST", LargeFlashSize + LowMidSize, UtestSize,
            0x400000, FlashBlockType.Utest, 0,
            "Test-, Security-, DCF- und Kunden-OTP-Daten"));

        return new Mpc5777cLayout(partitions, complete: true);
    }

    public FlashPartition? PartitionAt(long fileOffset) =>
        Partitions.FirstOrDefault(p => p.Contains(fileOffset));

    /// <summary>CPU-Adresse zu einem Datei-Offset, falls zuzuordnen.</summary>
    public long? ToCpu(long fileOffset) => PartitionAt(fileOffset)?.ToCpu(fileOffset);
}
