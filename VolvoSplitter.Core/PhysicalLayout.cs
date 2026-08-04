namespace VolvoSplitter.Core;

/// <summary>
/// Blockart eines physischen Flash-Bereichs. Die Werte sind bausteinspezifisch
/// und tragen ihre Herkunft im Kommentar — sie sind keine allgemeine Taxonomie.
/// </summary>
public enum FlashBlockType
{
    /// <summary>MPC5777C: 8 MiB Large Flash, 32 Sektoren zu 256 KiB, CPU 0x800000–0xFFFFFF.</summary>
    Large,

    /// <summary>MPC5777C: 64-KiB-Low-Block, CPU 0x000000–0x01FFFF.</summary>
    Low,

    /// <summary>MPC5777C: 64-KiB-Mid-Block, CPU 0x020000–0x03FFFF.</summary>
    Mid,

    /// <summary>MPC5777C: 16-KiB-UTEST-Block, CPU 0x400000–0x403FFF.</summary>
    Utest,

    /// <summary>TriCore: Programmflash (PMU) oder externer Flash am EBU.</summary>
    Program,

    /// <summary>TriCore: Datenflash (DFLASH).</summary>
    DataFlash,

    /// <summary>Im Abbild vorhanden, aber keiner Adresse zuzuordnen.</summary>
    Unknown
}

/// <summary>
/// Ein physischer Flash-Block, wie er im Abbild liegt: Datei-Offset und
/// — soweit rekonstruierbar — die zugehörige CPU-Adresse.
/// </summary>
/// <param name="CpuStart">Null, wenn der Bereich keiner CPU-Adresse zuzuordnen ist.</param>
public sealed record FlashPartition(string Label, long FileStart, long FileLength, long? CpuStart,
                                    FlashBlockType Type, int? RwwPartition, string ExampleUse)
{
    public long FileEnd => FileStart + FileLength;

    public bool Contains(long fileOffset) => fileOffset >= FileStart && fileOffset < FileEnd;

    /// <summary>
    /// Der Block trägt eine EEPROM-Emulation. Beim MPC5777C sind das die vier
    /// Low/Mid-Blöcke, beim TriCore der DFLASH. Rein beschreibend: was dort
    /// tatsächlich steht, entscheidet weiterhin der Inhalt.
    /// </summary>
    public bool EmulatedEeprom { get; init; }

    /// <summary>
    /// Einmal programmierbar: gesetzte Bits lassen sich nicht mehr löschen oder
    /// überschreiben. Beim MPC5777C ist das der UTEST-Block — dort stehen die
    /// Konfigurations-Fuses (DCF-Records) für Startart und Takt, die Zensur- und
    /// JTAG-/Nexus-Sperren, Boot-Schlüssel, Hardware-Kennungen und
    /// Herstellungsdaten (Referenzhandbuch-Addendum, UTEST-/DCF-Tabelle).
    ///
    /// Eine Eigenschaft des Bausteins, keine Aussage über den Inhalt: was dort
    /// im vorliegenden Abbild steht, sagt weiterhin der Zustand des Blocks.
    /// Für die Anzeige ist es der wichtigste Unterschied überhaupt — ein Fehler
    /// in gewöhnlichem Flash kostet einen Schreibvorgang, hier den Baustein.
    /// </summary>
    public bool Otp { get; init; }

    /// <summary>CPU-Adresse zu einem Datei-Offset in diesem Block.</summary>
    public long? ToCpu(long fileOffset) =>
        CpuStart is { } cpu && Contains(fileOffset) ? cpu + (fileOffset - FileStart) : null;

    public string FileRange => Hex.Range(FileStart, FileEnd);

    public string? CpuRange => CpuStart is { } cpu ? Hex.Range(cpu, cpu + FileLength) : null;
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

    /// <summary>Einmal programmierbar — siehe <see cref="FlashPartition.Otp"/>.</summary>
    public bool Otp => Partition.Otp;

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
/// Das physische Speicherlayout eines Bausteins, auf die vorliegende Datei
/// abgebildet: welcher Datei-Offset welcher CPU-Adresse entspricht und welche
/// Löschsektoren der Baustein hat.
///
/// Reine Daten, kein Verhalten — deshalb keine Schnittstelle und keine
/// Vererbung, sondern statische Erbauer je Baustein
/// (<see cref="Mpc5777cLayout"/>, <c>TriCore.TriCoreLayout</c>).
/// </summary>
public sealed class PhysicalLayout
{
    /// <summary>
    /// Spiegelbit der TriCore-Adressierung: <c>0x8…</c> ist der gecachte,
    /// <c>0xA…</c> der ungecachte Blick auf <em>denselben</em> Flash. Beim
    /// Umrechnen wird es maskiert, damit beide Schreibweisen gleich behandelt
    /// werden — die Infineon-Unterlagen nennen <c>0xA…</c>, die Bosch-Blockköpfe
    /// im Abbild nennen <c>0x8…</c>.
    ///
    /// Für den MPC5777C ist das folgenlos: dessen Adressen liegen unterhalb
    /// von <c>0x20000000</c>.
    /// </summary>
    public const long SegmentMirror = 0x20000000;

    public PhysicalLayout(IReadOnlyList<FlashPartition> partitions, bool complete, string sourceNote,
                          IReadOnlyList<FlashPartition>? eraseSectors = null)
    {
        Partitions = partitions;
        Complete = complete;
        SourceNote = sourceNote;
        EraseSectors = eraseSectors ?? [];
    }

    /// <summary>Grobe Einteilung: Bänke, Blöcke, Anhänge.</summary>
    public IReadOnlyList<FlashPartition> Partitions { get; }

    /// <summary>
    /// Feine Einteilung: die Löschsektoren des Bausteins. Dient allein der
    /// Anzeige — als Partitionen benutzt würden sie eine zusammenhängende
    /// Coderegion mit dem Namen des ersten berührten Sektors versehen.
    /// Leer, wenn für den Baustein keine Sektorkarte hinterlegt ist.
    /// </summary>
    public IReadOnlyList<FlashPartition> EraseSectors { get; }

    /// <summary>
    /// Die Abbildung ging vollständig auf: jedes Byte der Datei ist einer
    /// dokumentierten physischen Einheit zugeordnet, und deren Sektorkarte ist
    /// bekannt. Sonst bleibt ein Teil unzugeordnet oder ungegliedert.
    /// </summary>
    public bool Complete { get; }

    /// <summary>Woher die Karte stammt — steht so in Bericht und Oberfläche.</summary>
    public string SourceNote { get; }

    public FlashPartition? PartitionAt(long fileOffset) =>
        Partitions.FirstOrDefault(p => p.Contains(fileOffset));

    /// <summary>CPU-Adresse zu einem Datei-Offset, falls zuzuordnen.</summary>
    public long? ToCpu(long fileOffset) => PartitionAt(fileOffset)?.ToCpu(fileOffset);

    /// <summary>
    /// Datei-Offset zu einer CPU-Adresse — die Rückrichtung, die die
    /// Bosch-Blockkette braucht, um Zeigern zu folgen. Gecachte und ungecachte
    /// Adresse liefern denselben Offset.
    /// </summary>
    public long? ToFile(long cpuAddress)
    {
        long wanted = Normalize(cpuAddress);

        foreach (var partition in Partitions)
        {
            if (partition.CpuStart is not { } cpu) continue;

            long start = Normalize(cpu);
            if (wanted >= start && wanted < start + partition.FileLength)
                return partition.FileStart + (wanted - start);
        }
        return null;
    }

    /// <summary>Blendet das Spiegelbit aus, sodass 0x8… und 0xA… gleich sind.</summary>
    public static long Normalize(long cpuAddress) => cpuAddress & ~SegmentMirror;

    /// <summary>Löschsektor an einer Datei-Adresse, falls eine Sektorkarte hinterlegt ist.</summary>
    public FlashPartition? EraseSectorAt(long fileOffset) =>
        EraseSectors.FirstOrDefault(s => s.Contains(fileOffset));

    /// <summary>Beginnt an diesem Datei-Offset ein Löschsektor?</summary>
    public bool IsEraseSectorStart(long fileOffset) =>
        EraseSectors.Any(s => s.FileStart == fileOffset);
}
