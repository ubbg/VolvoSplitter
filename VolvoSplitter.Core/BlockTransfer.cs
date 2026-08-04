using VolvoSplitter.Core.TriCore;

namespace VolvoSplitter.Core;

/// <summary>Gewicht eines Befunds beim Blockübertrag.</summary>
public enum TransferSeverity
{
    /// <summary>Feststellung, die nichts in Frage stellt.</summary>
    Observation,

    /// <summary>Der Übertrag ist zulässig, aber es gibt etwas zu wissen.</summary>
    Warning,

    /// <summary>Der Übertrag findet nicht statt.</summary>
    Rejection
}

/// <summary>
/// Die benannten Regeln des Blockübertrags. Jeder Befund trägt eine davon,
/// damit Tests auf die Regel prüfen und nicht auf einen Satz — und damit
/// niemand eine Bedingung aufweichen kann, ohne den Wert anzufassen, der sie
/// benennt.
/// </summary>
public enum TransferRule
{
    /// <summary>Quelle und Ziel sind dasselbe Abbild.</summary>
    SameImage,

    /// <summary>Der Quellblock lässt sich nicht vollständig lesen.</summary>
    SourceUnreadable,

    /// <summary>Verschiedene Containerformate — ein Block der einen Art hat in der anderen kein Gegenstück.</summary>
    ContainerMismatch,

    /// <summary>Im Ziel liegt an dieser CPU-Adresse kein Block.</summary>
    NoCounterpart,

    /// <summary>Im Ziel liegen an dieser CPU-Adresse mehrere Blöcke.</summary>
    AmbiguousCounterpart,

    /// <summary>An derselben Adresse liegt im Ziel ein Block anderer Art.</summary>
    KindMismatch,

    /// <summary>Der Zielblock ist einmal programmierbar.</summary>
    TargetIsOtp,

    /// <summary>Der Block passt nicht an die Stelle — Nachbar, Bankgrenze oder belegter Platz.</summary>
    DoesNotFit,

    /// <summary>Die Quelle ist kleiner; der Rest des Zielblocks wird gelöscht.</summary>
    SizeSmaller,

    /// <summary>Die Quelle ist größer und wächst in gelöschten Platz hinein.</summary>
    SizeLarger,

    /// <summary>Softwarekennung bzw. Teilenummer weichen ab.</summary>
    IdentifierDiffers,

    /// <summary>Die Prüfsumme der Quelle geht selbst nicht auf oder wurde nie gestellt.</summary>
    SourceChecksumNotVerified,

    /// <summary>Die Quelle trägt das OTP-Kennzeichen, der Zielblock trug es nicht.</summary>
    OtpFlagCarriedOver,

    /// <summary>Derselbe Block liegt in beiden Dateien an verschiedenen Offsets — der Normalfall.</summary>
    FileOffsetDiffers,

    /// <summary>Der übernommene <c>nextSector</c> zeigt im Ziel auf keinen Blockkopf.</summary>
    NextSectorLeavesChain
}

/// <summary>Ein einzelner Befund samt ausgeschriebener Begründung.</summary>
public sealed record TransferFinding(TransferRule Rule, TransferSeverity Severity, string Text);

/// <summary>
/// Was beim Übertrag eines Blocks von einem Abbild ins andere geschehen würde,
/// und was daran auffällt — geprüft, aber noch nicht ausgeführt.
///
/// Ein Plan ist nur aus <see cref="BlockTransfer.Prepare"/> zu bekommen. Damit
/// steht „ohne Prüfung kein Übertrag" im Typsystem und nicht in einer
/// Übereinkunft, an die sich der nächste Aufrufer erinnern müsste.
/// </summary>
public sealed record BlockTransferPlan
{
    internal BlockTransferPlan(FlashDump source, SectorInfo sourceBlock, FlashDump target,
                               SectorInfo? targetBlock, long tailErased,
                               IReadOnlyList<TransferFinding> findings)
    {
        Source = source;
        SourceBlock = sourceBlock;
        Target = target;
        TargetBlock = targetBlock;
        TailErased = tailErased;
        Findings = findings;
    }

    public FlashDump Source { get; }
    public SectorInfo SourceBlock { get; }
    public FlashDump Target { get; }

    /// <summary>Der Block, der überschrieben würde. Null, wenn keiner gefunden wurde.</summary>
    public SectorInfo? TargetBlock { get; }

    /// <summary>Bytes, die hinter der Kopie bis zum bisherigen Blockende gelöscht würden.</summary>
    public long TailErased { get; }

    public IReadOnlyList<TransferFinding> Findings { get; }

    public IEnumerable<TransferFinding> Rejections =>
        Findings.Where(f => f.Severity == TransferSeverity.Rejection);

    public IEnumerable<TransferFinding> Warnings =>
        Findings.Where(f => f.Severity == TransferSeverity.Warning);

    /// <summary>Der Übertrag ist zulässig.</summary>
    public bool Possible => TargetBlock is not null && !Rejections.Any();

    /// <summary>Alle Ablehnungsgründe in einem Satz — für Anzeige und Ausnahmetext.</summary>
    public string RejectionText => string.Join("  ", Rejections.Select(r => r.Text));
}

/// <summary>
/// Eine Prüfsummenstruktur im Ziel vor und nach dem Übertrag. Null heißt
/// „nicht nachgerechnet", nicht „in Ordnung" — wie in
/// <see cref="BoschChecksumStructure.Ok"/>.
/// </summary>
public sealed record ChecksumChange(long BlockStart, string BlockLabel, int Index,
                                    bool? Before, bool? After)
{
    public bool Broke => Before == true && After != true;
    public bool Healed => Before != true && After == true;
}

/// <summary>
/// Was der Übertrag bewirkt hat — gemessen am Abbild danach, nicht abgeleitet
/// aus der Absicht.
/// </summary>
/// <param name="HeaderStillValid">
/// Der übernommene Block wird am Zielort weiterhin als Block gelesen. Fällt das
/// weg, ist etwas grundsätzlich schief, und es steht zuoberst.
/// </param>
/// <param name="ChecksumChanges">
/// Prüfsummenstrukturen des <em>ganzen</em> Zielabbilds, deren Ergebnis sich
/// geändert hat — auch die anderer Blöcke, deren geprüfter Bereich in den
/// beschriebenen Teil hineinreicht.
/// </param>
public sealed record BlockTransferResult(
    BlockTransferPlan Plan,
    long BytesWritten,
    long TailErased,
    bool HeaderStillValid,
    SectorStatus StatusAfter,
    IReadOnlyList<ChecksumChange> ChecksumChanges,
    IReadOnlyList<TransferFinding> Findings,
    uint? CvnBefore,
    uint? CvnAfter)
{
    public IEnumerable<ChecksumChange> Broken => ChecksumChanges.Where(c => c.Broke);
    public IEnumerable<ChecksumChange> Healed => ChecksumChanges.Where(c => c.Healed);

    /// <summary>
    /// Die CVN ist eine andere als vorher. Keine Fehlermeldung: sie wird
    /// gelesen und ausgewiesen, nicht gestellt.
    /// </summary>
    public bool CvnChanged => CvnBefore != CvnAfter;
}

/// <summary>
/// Prüft, ob ein Block aus einem Abbild in ein anderes übernommen werden kann —
/// und was dabei auffällt. Fasst kein Byte an; das Schreiben ist Sache von
/// <see cref="FlashDump.CopyBlockFrom"/>, damit es bei der einen Arbeitskopie
/// bleibt, die es schon gibt.
///
/// <strong>Die Kopie geht ausschließlich an dieselbe CPU-Adresse.</strong> Das
/// ist nicht Vorsicht, sondern der Grund, warum der Vorgang überhaupt zulässig
/// ist: ein Bosch-Blockkopf trägt <c>blockEnd</c>, <c>nextSector</c>, zwei
/// Tabellenzeiger und je Prüfsummenstruktur zwei Bereichsgrenzen — alles
/// absolute CPU-Adressen. <see cref="BoschBlockChain"/> prüft
/// <c>blockEnd == blockStart + size − 4</c> ohne Toleranz; an eine andere
/// Adresse kopiert wäre der Block kein Block mehr. Bei gleicher Adresse bleibt
/// jedes dieser Felder gültig, und es muss kein einziges Byte umgeschrieben
/// werden.
///
/// Deshalb wird hier auch nichts umgeschrieben: keine Adressfelder, keine
/// Prüfsummen, keine CVN. Übernommen werden Bytes, die ein anderes Abbild schon
/// trägt; was danach aufgeht und was nicht, wird nachgerechnet und gemeldet.
/// </summary>
public static class BlockTransfer
{
    /// <param name="acceptOtpTarget">
    /// Einen einmal programmierbaren Zielblock zulassen. Der Befund verschwindet
    /// dabei nicht — er wird von der Ablehnung zur Warnung. Nur setzen, wo ein
    /// Mensch die Folge ausdrücklich bejaht hat.
    /// </param>
    public static BlockTransferPlan Prepare(FlashDump source, SectorInfo sourceBlock,
                                            FlashDump target, bool acceptOtpTarget = false)
    {
        var findings = new List<TransferFinding>();

        void Add(TransferRule rule, TransferSeverity severity, string text) =>
            findings.Add(new TransferFinding(rule, severity, text));

        BlockTransferPlan Stop() =>
            new(source, sourceBlock, target, null, 0, findings);

        if (ReferenceEquals(source, target))
        {
            Add(TransferRule.SameImage, TransferSeverity.Rejection,
                "Quelle und Ziel sind dasselbe geladene Abbild.");
            return Stop();
        }

        if (!sourceBlock.Present || sourceBlock.Length <= 0)
        {
            Add(TransferRule.SourceUnreadable, TransferSeverity.Rejection,
                $"Der Block {sourceBlock.Label} ist in der Quelle nicht gelesen worden.");
            return Stop();
        }

        if (sourceBlock.Truncated)
        {
            Add(TransferRule.SourceUnreadable, TransferSeverity.Rejection,
                $"Der Block {sourceBlock.Label} reicht in der Quelle über das Dateiende hinaus " +
                "und liegt dort nur gekürzt vor.");
            return Stop();
        }

        if (source.Profile.Container != target.Profile.Container ||
            source.Profile.Container == ContainerKind.None)
        {
            Add(TransferRule.ContainerMismatch, TransferSeverity.Rejection,
                $"Quelle ({source.Profile.FamilyName}) und Ziel ({target.Profile.FamilyName}) " +
                "haben verschiedene Containerformate — ein Block der einen Art hat in der " +
                "anderen kein Gegenstück.");
            return Stop();
        }

        // ------------------------------------------------------------------
        // Das Gegenstück: dieselbe CPU-Adresse. Normalize blendet das
        // TriCore-Spiegelbit aus (0x8… und 0xA… sind derselbe Flash); für die
        // MPC-Adressen ist es folgenlos.
        // ------------------------------------------------------------------
        long cpu = PhysicalLayout.Normalize(sourceBlock.CpuOffset);

        var candidates = target.Sectors
            .Where(s => s.Present && PhysicalLayout.Normalize(s.CpuOffset) == cpu)
            .ToList();

        if (candidates.Count == 0)
        {
            Add(TransferRule.NoCounterpart, TransferSeverity.Rejection,
                $"Im Ziel liegt an CPU {Hex.Addr(cpu)} kein Block. {WhatLiesAt(target, cpu)}");
            return Stop();
        }

        if (candidates.Count > 1)
        {
            Add(TransferRule.AmbiguousCounterpart, TransferSeverity.Rejection,
                $"Im Ziel liegen an CPU {Hex.Addr(cpu)} {candidates.Count} Blöcke — nicht " +
                "eindeutig. Geraten wird hier nicht.");
            return Stop();
        }

        var hit = candidates[0];

        // ------------------------------------------------------------------
        // Blockart. Bei Bosch über die Kennung aus dem Blockkopf, nicht über
        // SectorKind: dort fallen 0x40, 0x50, 0xA0 und 0xB0 alle auf „ASW".
        // ------------------------------------------------------------------
        if (source.Profile.Container == ContainerKind.BoschBlockChain)
        {
            var from = BoschBlockAt(source, sourceBlock);
            var to = BoschBlockAt(target, hit);

            if (from is null || to is null)
            {
                Add(TransferRule.SourceUnreadable, TransferSeverity.Rejection,
                    "Der Blockkopf ließ sich in der gelesenen Kette nicht wiederfinden.");
                return Stop();
            }

            if (from.Id != to.Id)
            {
                Add(TransferRule.KindMismatch, TransferSeverity.Rejection,
                    $"An CPU {Hex.Addr(cpu)} liegt im Ziel „{to.IdName}\" (0x{to.Id:X2}), " +
                    $"in der Quelle „{from.IdName}\" (0x{from.Id:X2}) — andere Blockart.");
                return new BlockTransferPlan(source, sourceBlock, target, hit, 0, findings);
            }
        }
        else if (sourceBlock.Kind != hit.Kind)
        {
            Add(TransferRule.KindMismatch, TransferSeverity.Rejection,
                $"An CPU {Hex.Addr(cpu)} liegt im Ziel „{hit.Label}\", in der Quelle " +
                $"„{sourceBlock.Label}\" — andere Art.");
            return new BlockTransferPlan(source, sourceBlock, target, hit, 0, findings);
        }

        // ------------------------------------------------------------------
        // OTP im Ziel. In der Vorgabe eine Ablehnung: ein einmal
        // programmierter Block lässt sich im Baustein nicht neu beschreiben.
        // Ein Abbild, das dort etwas anderes trägt, ist auf dem echten Gerät
        // nicht herstellbar — es stillschweigend zu erzeugen hieße, ein
        // Ergebnis zu liefern, das nur auf der Festplatte existiert.
        // ------------------------------------------------------------------
        bool targetOtp = hit.Otp ||
            target.Layout?.Partitions.Any(p => p.Otp && p.Contains(hit.Start)) == true;

        if (targetOtp)
            Add(TransferRule.TargetIsOtp,
                acceptOtpTarget ? TransferSeverity.Warning : TransferSeverity.Rejection,
                $"Der Zielblock bei {Hex.Addr(hit.Start)} ist einmal programmierbar (OTP). " +
                "Im Steuergerät ließe er sich nicht noch einmal beschreiben.");

        if (sourceBlock.Otp && !hit.Otp)
            Add(TransferRule.OtpFlagCarriedOver, TransferSeverity.Warning,
                "Der Quellblock trägt das OTP-Kennzeichen und nimmt es mit — der Zielblock " +
                "führt es danach ebenfalls.");

        // ------------------------------------------------------------------
        // Größe und Platz
        // ------------------------------------------------------------------
        long size = sourceBlock.Length;
        long room = RoomAt(target, hit);
        long tail = 0;

        if (size > room)
            Add(TransferRule.DoesNotFit, TransferSeverity.Rejection,
                $"Der Block der Quelle ist {size:N0} B groß; im Ziel stehen ab " +
                $"{Hex.Addr(hit.Start)} nur {room:N0} B bis zum nächsten Block, zur " +
                "Blockgrenze des Bausteins oder zum Dateiende.");
        else if (size > hit.Length)
        {
            if (target.IsErased(hit.End, size - hit.Length))
                Add(TransferRule.SizeLarger, TransferSeverity.Warning,
                    $"Die Quelle ist {size - hit.Length:N0} B größer als der Zielblock; der " +
                    "Überstand wächst in gelöschten Platz hinein.");
            else
                Add(TransferRule.DoesNotFit, TransferSeverity.Rejection,
                    $"Die Quelle ist {size - hit.Length:N0} B größer als der Zielblock, und " +
                    $"der Platz dahinter ({Hex.Range(hit.End, hit.Start + size)}) ist nicht " +
                    "gelöscht. Er würde überschrieben, ohne dass bekannt ist, was dort steht.");
        }
        else if (size < hit.Length)
        {
            tail = hit.Length - size;
            Add(TransferRule.SizeSmaller, TransferSeverity.Warning,
                $"Die Quelle ist {tail:N0} B kleiner als der Zielblock; der Rest bis " +
                $"{Hex.Addr(hit.End)} wird auf 0xFF gesetzt.");
        }

        // ------------------------------------------------------------------
        // Beobachtungen und Warnungen, die den Übertrag nicht verhindern
        // ------------------------------------------------------------------
        if (sourceBlock.PartNumber != hit.PartNumber)
            Add(TransferRule.IdentifierDiffers, TransferSeverity.Warning,
                $"Kennung der Quelle „{sourceBlock.PartNumber}\", des Ziels " +
                $"„{hit.PartNumber}\" — nach dem Übertrag stehen im Ziel Stände " +
                "verschiedener Herkunft nebeneinander.");

        if (!sourceBlock.CrcOk)
            Add(TransferRule.SourceChecksumNotVerified, TransferSeverity.Warning,
                sourceBlock.ChecksumNotStamped
                    ? "Für den Quellblock wurde nie eine Prüfsumme gestellt. Das bleibt nach " +
                      "dem Übertrag so — es wird nichts gestellt."
                    : $"Die Prüfsumme des Quellblocks weicht ab (Datei 0x{sourceBlock.CrcStored:X8}, " +
                      $"berechnet 0x{sourceBlock.CrcComputed:X8}). Sie wird unverändert " +
                      "mitgenommen und weicht danach auch im Ziel ab.");

        if (sourceBlock.Start != hit.Start)
            Add(TransferRule.FileOffsetDiffers, TransferSeverity.Observation,
                $"Quelle bei {Hex.Addr(sourceBlock.Start)}, Ziel bei {Hex.Addr(hit.Start)} — " +
                $"dieselbe CPU-Adresse {Hex.Addr(cpu)}. Verschiedene Fensteranfänge sind üblich.");

        return new BlockTransferPlan(source, sourceBlock, target, hit, tail, findings);
    }

    /// <summary>
    /// Sagt, was im Ziel an der Adresse liegt, an der kein Block gefunden wurde.
    /// Eine Ablehnung ohne Begründung wäre nur ein Nein.
    /// </summary>
    private static string WhatLiesAt(FlashDump target, long cpu)
    {
        if (target.Layout?.ToFile(cpu) is not { } file)
            return "Diese Adresse lässt sich im Ziel keinem Datei-Offset zuordnen.";

        if (target.Regions.FirstOrDefault(r => file >= r.Start && file < r.End) is { } region)
            return $"Bei {Hex.Addr(file)} steht {region.Label} ({region.AddressRange}), " +
                   "aber kein Blockkopf.";

        if (target.Layout.PartitionAt(file) is { } partition)
            return $"Bei {Hex.Addr(file)} liegt {partition.Label}; dort steht kein Blockkopf.";

        return $"Bei {Hex.Addr(file)} steht kein Blockkopf.";
    }

    /// <summary>
    /// Platz ab dem Zielblock: bis zum nächsten Block, bis zur Grenze des
    /// physischen Blocks oder bis zum Dateiende — je nachdem, was zuerst kommt.
    ///
    /// Nicht <see cref="FlashDump.AvailableSpace"/>: das kennt die Grenzen des
    /// Bausteins nicht. Ein Bosch-Blockkopf wird aber nur angenommen, wenn Kopf
    /// und Blockende in derselben Bank liegen — ein bankübergreifender Block
    /// wäre nach dem Übertrag keiner mehr.
    /// </summary>
    private static long RoomAt(FlashDump target, SectorInfo hit)
    {
        long limit = target.Size;

        foreach (var other in target.Sectors)
            if (other.Present && other.Start > hit.Start && other.Start < limit)
                limit = other.Start;

        if (target.Layout?.PartitionAt(hit.Start) is { } partition && partition.FileEnd < limit)
            limit = partition.FileEnd;

        return limit - hit.Start;
    }

    /// <summary>Der gelesene Bosch-Block zu einem Sektor, oder null.</summary>
    private static BoschBlock? BoschBlockAt(FlashDump dump, SectorInfo sector) =>
        dump.Chain.Blocks.FirstOrDefault(b => b.FileStart == sector.Start);
}
