using System.IO;
using VolvoSplitter.Core.TriCore;

namespace VolvoSplitter.Core;

/// <summary>Fahrzeugdaten aus dem Parameter-Sektor.</summary>
public sealed record VehicleInfo(string Vin, string ChassisNumber, string Maker);

/// <summary>
/// Ein geladenes Flash-Abbild. Hält eine Arbeitskopie im Speicher, damit
/// Sektoren ersetzt und Prüfsummen korrigiert werden können, ohne die
/// Originaldatei anzufassen.
///
/// Welches Steuergerät vorliegt, entscheidet <see cref="EcuDetector"/> an
/// Belegen im Abbild; das Ergebnis steht in <see cref="Detection"/> und
/// <see cref="Profile"/>. Verändernde Vorgänge gibt es nur für Profile, deren
/// <see cref="EcuProfile.SupportsWriteBack"/> gesetzt ist — TriCore-Abbilder
/// werden ausdrücklich nur gelesen.
/// </summary>
public sealed class FlashDump
{
    private readonly byte[] _data;

    private FlashDump(string path, byte[] data, EcuReport? report, string? forceProfile)
    {
        SourcePath = path;
        _data = data;
        Report = report;

        Detection = Detect(data, report, forceProfile);
        Profile = Detection.Profile;
        Family = Profile.Family ?? FlashFormat.DetectFamily(data.LongLength);
        Layout = EcuProfiles.LayoutFor(Profile, data.LongLength);
        Sectors = [];
    }

    private static Detection Detect(byte[] data, EcuReport? report, string? forceProfile)
    {
        if (forceProfile is null) return EcuDetector.Identify(data, report);

        var forced = EcuProfiles.ByKey(forceProfile, data.LongLength)
            ?? throw new ArgumentException(
                $"Unbekanntes Profil „{forceProfile}\". Bekannt sind: " +
                string.Join(", ", EcuProfiles.Keys) + ".", nameof(forceProfile));

        return new Detection(forced, 0, 0,
            [$"Profil „{forceProfile}\" von Hand vorgegeben — keine Erkennung gelaufen"],
            Ambiguous: false);
    }

    public string SourcePath { get; }
    public string FileName => Path.GetFileName(SourcePath);
    public string Directory => Path.GetDirectoryName(Path.GetFullPath(SourcePath)) ?? ".";
    public long Size => _data.LongLength;

    /// <summary>
    /// TRW-Familie. Bei TriCore- und unbekannten Abbildern ohne Aussagekraft —
    /// dort steht die Gerätefamilie in <see cref="Profile"/>.
    /// </summary>
    public EcuFamily Family { get; }

    /// <summary>Was über das Steuergerät bekannt ist.</summary>
    public EcuProfile Profile { get; }

    /// <summary>Wie das Profil zustande kam, samt Belegliste.</summary>
    public Detection Detection { get; }

    /// <summary>Arbeitskopie wurde verändert und ist noch nicht gespeichert.</summary>
    public bool IsModified { get; private set; }

    public IReadOnlyList<SectorInfo> Sectors { get; private set; }

    /// <summary>Belegte Bereiche ohne Sektorkopf — Programmcode, opake Blöcke, NVM-Daten.</summary>
    public IReadOnlyList<FlashRegion> Regions { get; private set; } = [];

    /// <summary>
    /// Physisches Speicherlayout des Mikrocontrollers, falls die Dateigröße dazu
    /// passt. Null heißt: Datei-Offsets bleiben die einzige belastbare Aussage.
    /// </summary>
    public PhysicalLayout? Layout { get; }

    /// <summary>Die physischen Blöcke des Bausteins mit ihrem Zustand im Abbild.</summary>
    public IReadOnlyList<PartitionInfo> Partitions { get; private set; } = [];

    /// <summary>Gelesene Bosch-Blockkette. Leer, wenn das Profil keine vorsieht.</summary>
    public BoschChainResult Chain { get; private set; } = BoschChainResult.Empty;

    /// <summary>Kennungen aus einem Bosch-Abbild. Null bei allen anderen Profilen.</summary>
    public BoschIdentity? Identity { get; private set; }

    /// <summary>Größe des Flash-Bereichs, den das Profil beschreibt.</summary>
    public long FlashSize => Profile.FlashSize;

    public VehicleInfo? Vehicle { get; private set; }

    /// <summary>Begleitende Protokolldatei des Auslesegeräts, falls vorhanden.</summary>
    public EcuReport? Report { get; }

    public ReadOnlySpan<byte> Raw => _data;

    public static FlashDump Load(string path, bool fixedAddressesOnly = false,
                                 string? forceProfile = null)
    {
        var dump = new FlashDump(path, File.ReadAllBytes(path), EcuReport.FindFor(path), forceProfile);
        dump.Analyze(fixedAddressesOnly);
        return dump;
    }

    /// <summary>
    /// Baut ein Abbild aus bereits im Speicher liegenden Bytes. Der Name dient
    /// nur der Benennung; eine begleitende Protokolldatei wird nicht gesucht.
    /// Für Aufrufer, die die Daten schon halten, und für Tests.
    /// </summary>
    public static FlashDump FromBytes(byte[] data, string name = "memory.mpc",
                                      bool fixedAddressesOnly = false, string? forceProfile = null)
    {
        var dump = new FlashDump(name, data, null, forceProfile);
        dump.Analyze(fixedAddressesOnly);
        return dump;
    }

    // ------------------------------------------------------------------
    // Analyse
    // ------------------------------------------------------------------

    /// <param name="fixedAddressesOnly">
    /// Nur die fest verdrahteten Adressen lesen — das Verhalten der V2. Sonst
    /// wird das ganze Abbild durchsucht, sodass auch verschobene Blöcke
    /// gefunden werden. Gilt nur für das TRW-Sektorformat.
    /// </param>
    public void Analyze(bool fixedAddressesOnly = false)
    {
        var sectors = Profile.Container switch
        {
            ContainerKind.TrwSector => ReadTrwSectors(fixedAddressesOnly),
            ContainerKind.BoschBlockChain => ReadBoschBlocks(),
            _ => []
        };

        Sectors = sectors;
        Vehicle = Profile.Container == ContainerKind.TrwSector ? ReadVehicleInfo() : null;
        Regions = RegionScanner.Scan(_data, FlashSize, sectors, Layout, Profile);
        Partitions = DescribePartitions();

        if (Profile.Container != ContainerKind.TrwSector) return;

        foreach (var sector in sectors)
        {
            if (sector.Present)
                sector.ChecksumCopies = FindChecksumCopies(sector);
            else
                sector.MissingReason = ExplainMissing(sector);
        }
    }

    private List<SectorInfo> ReadTrwSectors(bool fixedAddressesOnly)
    {
        var slots = fixedAddressesOnly
            ? Profile.Slots.ToList()
            : TrwContainer.DiscoverSlots(_data, Profile.Slots);

        return slots.OrderBy(s => s.Start)
                    .Select(slot => TrwContainer.ReadSector(_data, slot))
                    .ToList();
    }

    /// <summary>
    /// Liest die Bosch-Blockkette. Die Kette wurde bei der Erkennung schon
    /// gelaufen; sie wird hier nur dann erneut gelesen, wenn das Profil von
    /// Hand vorgegeben wurde — oder wenn die Arbeitskopie inzwischen verändert
    /// wurde.
    ///
    /// Der zweite Fall ist der wichtigere: das Ergebnis der Erkennung ist ein
    /// Bild des Zustands <em>vor</em> der Änderung. Es danach weiterzureichen
    /// hieße, Prüfsummen zu melden, die zu anderen Bytes gehören — und genau
    /// die Prüfsummen sind es, wegen derer nach einem Blockübertrag überhaupt
    /// jemand hinsieht.
    /// </summary>
    private List<SectorInfo> ReadBoschBlocks()
    {
        if (Layout is null) return [];

        Chain = !IsModified && Detection.Chain.Blocks.Count > 0
            ? Detection.Chain
            : BoschBlockChain.Read(_data, Layout);

        Identity = !IsModified && Detection.Identity is { } known
            ? known
            : BoschIdentity.Scan(_data, Chain.Variant, Chain.VariantOffset ?? 0);

        return BoschBlockChain.ToSectors(Chain.Blocks);
    }

    /// <summary>
    /// Sagt, was an der Adresse eines fehlenden Sektors tatsächlich liegt,
    /// statt pauschal "leer oder verschlüsselt" zu melden.
    /// </summary>
    private string ExplainMissing(SectorInfo sector)
    {
        if (sector.Start + FlashFormat.MinSectorLength > _data.LongLength)
            return $"Adresse {Hex.Addr(sector.Start)} liegt außerhalb der Datei";

        var region = Regions.FirstOrDefault(r => sector.Start >= r.Start && sector.Start < r.End);

        if (region is null)
            return $"Kein Sektorkopf bei {Hex.Addr(sector.Start)} — Bereich ist gelöscht (0xFF)";

        string what = region.Kind switch
        {
            RegionKind.Code => "dort steht Programmcode im Klartext, aber ohne Sektorkopf",
            RegionKind.Opaque => "dort steht Inhalt in unbekanntem Format",
            RegionKind.NvmData => "dort stehen laufzeitveränderte NVM-Daten",
            _ => "dort stehen Daten ohne Sektorkopf"
        };

        return $"Kein Sektorkopf bei {Hex.Addr(sector.Start)} — {what} " +
               $"({region.AddressRange}, {region.SizeText})";
    }

    /// <summary>
    /// Bewertet jeden physischen Block: gelöscht, nicht ausgelesen oder belegt.
    /// Das trennt die EEPROM-Emulationsblöcke und UTEST voneinander, statt alles
    /// hinter dem eigentlichen Programmflash in einen Topf zu werfen.
    /// </summary>
    private IReadOnlyList<PartitionInfo> DescribePartitions()
    {
        if (Layout is null) return [];

        var result = new List<PartitionInfo>();

        foreach (var partition in Layout.Partitions)
        {
            long end = Math.Min(partition.FileEnd, _data.LongLength);

            if (BinaryHeuristics.IsErased(_data, partition.FileStart, end - partition.FileStart))
            {
                // UTEST kann auf einem Seriengerät nicht leer sein: NXP
                // programmiert dort ab Werk Sensorkalibrierung und Chip-Kennung
                // (Referenzhandbuch, Tabelle 4-3). Vollständig 0xFF heißt also,
                // dass das Auslesegerät den Bereich nicht erfasst hat.
                result.Add(new PartitionInfo(partition,
                    partition.Type == FlashBlockType.Utest ? PartitionState.NotRead : PartitionState.Erased,
                    partition.Type == FlashBlockType.Utest
                        ? "Vollständig 0xFF — vom Auslesegerät nicht erfasst. UTEST trägt ab Werk " +
                          "Sensorkalibrierung und Chip-Kennung und kann nicht leer sein."
                        : partition.EmulatedEeprom
                            ? "Vollständig 0xFF — freier Block. Bei der EEPROM-Emulation ist das der " +
                              "mögliche Tauschpartner für einen Block-Swap."
                            : "Vollständig 0xFF — gelöscht."));
                continue;
            }

            var inside = new List<string>();
            inside.AddRange(Sectors.Where(s => s.Present && partition.Contains(s.Start))
                                   .Select(s => $"Sektor {s.LabelText}"));
            inside.AddRange(Regions.Where(r => partition.Contains(r.Start))
                                   .Select(r => r.Label));

            result.Add(new PartitionInfo(partition, PartitionState.Occupied,
                inside.Count > 0
                    ? string.Join(", ", inside)
                    : "Belegt, Inhalt nicht weiter eingeordnet"));
        }

        return result;
    }

    /// <summary>
    /// Sucht den Prüfwert eines Sektors an anderen Stellen im Abbild. Das
    /// Steuergerät hält Kopien in Tabellen — im EEPROM-Bereich und am Ende des
    /// ASW-Codes. Wer nur den Trailer korrigiert, lässt die Kopien veralten.
    /// </summary>
    public IReadOnlyList<long> FindChecksumCopies(SectorInfo sector) =>
        FindUInt32Be(sector.CrcStored,
                     skipFrom: sector.End - FlashFormat.CrcTrailerLen,
                     skipTo: sector.End);

    /// <summary>Alle Fundstellen eines 32-Bit-Werts (big endian) im Abbild.</summary>
    public IReadOnlyList<long> FindUInt32Be(uint value, long skipFrom = -1, long skipTo = -1) =>
        FindBytes(ByteOrder.Pattern(value, Endianness.Big), skipFrom, skipTo);

    /// <summary>Alle Fundstellen einer Bytefolge im Abbild.</summary>
    public IReadOnlyList<long> FindBytes(ReadOnlySpan<byte> pattern, long skipFrom = -1,
                                         long skipTo = -1)
    {
        var hits = new List<long>();
        if (pattern.Length == 0) return hits;

        for (int i = 0; i + pattern.Length <= _data.Length; i++)
        {
            if (!_data.AsSpan(i, pattern.Length).SequenceEqual(pattern)) continue;
            if (i >= skipFrom && i < skipTo) continue;
            hits.Add(i);
        }
        return hits;
    }

    private VehicleInfo? ReadVehicleInfo()
    {
        var param = Sectors.FirstOrDefault(s => s.Kind == SectorKind.Parameter && s.Present);
        if (param is null || param.Length < FlashFormat.VinOffset + FlashFormat.VinLength)
            return null;

        string maker = TrwContainer.Ascii(_data, param.Start + FlashFormat.MakerOffset,
                                          FlashFormat.MakerLength);
        if (maker != "VOLVO")
            return null;

        string chassis = TrwContainer.Ascii(_data, param.Start + FlashFormat.ChassisOffset,
                                            FlashFormat.ChassisLength);
        string vin = TrwContainer.Ascii(_data, param.Start + FlashFormat.VinOffset,
                                        FlashFormat.VinLength);

        // "B    904194" -> "B 904194"
        chassis = string.Join(' ', chassis.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        return new VehicleInfo(vin.Trim(), chassis, maker);
    }

    // ------------------------------------------------------------------
    // Ausgabe
    // ------------------------------------------------------------------

    public byte[] SectorBytes(SectorInfo sector) =>
        _data.AsSpan((int)sector.Start, (int)sector.Length).ToArray();

    /// <summary>
    /// Ist dieser Bereich der Arbeitskopie vollständig gelöscht (0xFF)? Fragt
    /// <see cref="BlockTransfer"/>, bevor ein Block über sein bisheriges Ende
    /// hinaus wächst: gelöschter Platz darf beschrieben werden, belegter nicht,
    /// solange niemand weiß, was dort steht.
    /// </summary>
    public bool IsErased(long start, long length) =>
        start >= 0 && length >= 0 && start + length <= _data.LongLength &&
        BinaryHeuristics.IsErased(_data, start, length);

    /// <summary>Schreibt den Sektor als Rohdatei. Eine abweichende Prüfsumme
    /// verhindert das nicht — sie wird nur gemeldet.</summary>
    public string ExtractSector(SectorInfo sector, string? targetDirectory = null)
    {
        string path = Path.Combine(targetDirectory ?? Directory, sector.OutputName);
        File.WriteAllBytes(path, SectorBytes(sector));
        return path;
    }

    /// <summary>Schreibt den Sektor als Motorola-S-Record mit den echten CPU-Adressen.</summary>
    public string ExportSRecord(SectorInfo sector, string path)
    {
        SRecord.Write(path, SectorBytes(sector), sector.CpuOffset, sector.OutputName);
        return path;
    }

    // ------------------------------------------------------------------
    // Änderungen an der Arbeitskopie
    // ------------------------------------------------------------------

    /// <summary>
    /// Wirft, wenn das Profil kein Zurückschreiben vorsieht. Für TriCore-Abbilder
    /// ist das keine Lücke, sondern eine Festlegung: Prüfsummen werden gerechnet
    /// und gemeldet, nicht gestellt.
    /// </summary>
    private void EnsureWriteBack()
    {
        if (Profile.SupportsWriteBack) return;

        throw new InvalidOperationException(
            $"Für das Profil „{Profile.FamilyName}\" ist Zurückschreiben nicht vorgesehen. " +
            "Prüfsummen werden gerechnet und gemeldet, aber nicht gestellt.");
    }

    /// <summary>
    /// Wirft, wenn das Profil keinen Blockübertrag vorsieht. Das ist ein
    /// eigener Wächter und nicht <see cref="EnsureWriteBack"/>: hier wird nichts
    /// gerechnet und nichts gestellt, sondern ein Block übernommen, den ein
    /// anderes Abbild schon trägt.
    /// </summary>
    private void EnsureBlockTransfer()
    {
        if (Profile.SupportsBlockTransfer) return;

        throw new InvalidOperationException(
            $"Für das Profil „{Profile.FamilyName}\" ist kein Blockübertrag vorgesehen: " +
            "ohne erkannte Blockstruktur gibt es keinen Block, den man übertragen könnte.");
    }

    /// <summary>
    /// Wirft, wenn es für dieses Profil überhaupt keinen verändernden Vorgang
    /// gibt — dann gibt es auch nichts zu speichern. Bewusst weiter gefasst als
    /// <see cref="EnsureWriteBack"/>: ein übertragener Block muss sich sichern
    /// lassen, sonst wäre der Übertrag folgenlos.
    /// </summary>
    private void EnsureSaveable()
    {
        if (Profile.SupportsSaving) return;

        throw new InvalidOperationException(
            $"Für das Profil „{Profile.FamilyName}\" gibt es keinen verändernden Vorgang — " +
            "also auch nichts zu speichern.");
    }

    /// <summary>Ergebnis einer Prüfsummenkorrektur.</summary>
    /// <param name="OldCrc">Wert, der vorher im Trailer stand.</param>
    /// <param name="NewCrc">Neu berechneter Wert.</param>
    /// <param name="StaleCopies">
    /// Stellen, an denen der alte Wert noch steht und nicht mitkorrigiert wurde.
    /// </param>
    public sealed record CrcRepair(uint OldCrc, uint NewCrc, IReadOnlyList<long> StaleCopies);

    /// <summary>
    /// Berechnet die CRC32 des Sektors neu und trägt sie in den Trailer ein.
    /// Kopien des alten Werts an anderen Stellen bleiben unangetastet und
    /// werden zurückgemeldet.
    /// </summary>
    public CrcRepair RepairCrc(SectorInfo sector)
    {
        EnsureWriteBack();

        long crcPos = sector.Start + sector.Length - FlashFormat.CrcTrailerLen;
        uint before = ByteOrder.ReadUInt32(_data, crcPos, Endianness.Big);

        uint crc = TrwContainer.SectorCrc(_data, sector.Start, sector.Length);
        ByteOrder.WriteUInt32(_data, crcPos, crc, Endianness.Big);
        IsModified = true;

        var stale = before == crc
            ? []
            : FindUInt32Be(before, skipFrom: crcPos, skipTo: crcPos + FlashFormat.CrcTrailerLen);

        return new CrcRepair(before, crc, stale);
    }

    /// <summary>Trägt einen Prüfwert an einer beliebigen Stelle ein.</summary>
    public void PatchUInt32Be(long offset, uint value)
    {
        EnsureWriteBack();
        ByteOrder.WriteUInt32(_data, offset, value, Endianness.Big);
        IsModified = true;
    }

    /// <summary>Ergebnis eines Sektor-Ersetzens.</summary>
    /// <param name="HeaderFound">Die Ersatzdatei trägt selbst einen Sektorkopf.</param>
    /// <param name="ExpectedCpuOffset">CPU-Offset des ersetzten Sektors.</param>
    /// <param name="ActualCpuOffset">CPU-Offset laut Kopf der Ersatzdatei, falls lesbar.</param>
    public sealed record SectorReplacement(bool HeaderFound, long ExpectedCpuOffset, long? ActualCpuOffset)
    {
        /// <summary>Die Ersatzdatei nennt einen anderen CPU-Offset als der ersetzte Sektor.</summary>
        public bool CpuOffsetChanged => ActualCpuOffset is { } actual && actual != ExpectedCpuOffset;
    }

    /// <summary>
    /// Ersetzt einen Sektor durch den Inhalt einer Datei. Ist der neue Sektor
    /// kürzer, wird der Rest bis zum bisherigen Ende auf 0xFF gesetzt. Die
    /// Prüfsumme wird nur neu berechnet, wenn die Ersatzdatei selbst einen
    /// gültigen Sektorkopf trägt — Länge und CPU-Offset stammen dann aus diesem
    /// Kopf, nicht aus dem ersetzten Sektor.
    /// </summary>
    public SectorReplacement ReplaceSector(SectorInfo sector, byte[] replacement, bool repairCrc)
    {
        EnsureWriteBack();

        long available = AvailableSpace(sector);
        if (replacement.LongLength > available)
            throw new InvalidOperationException(
                $"Der Sektor passt nicht: {replacement.Length:N0} B, verfügbar sind " +
                $"{available:N0} B bis zum nächsten Sektor bzw. Dateiende.");

        if (replacement.Length < FlashFormat.MinSectorLength)
            throw new InvalidOperationException(
                $"Die Datei ist zu kurz für einen Sektor (mindestens {FlashFormat.MinSectorLength} B).");

        replacement.CopyTo(_data, (int)sector.Start);

        long oldEnd = sector.Start + sector.Length;
        long newEnd = sector.Start + replacement.LongLength;
        for (long i = newEnd; i < oldEnd; i++)
            _data[i] = 0xFF;

        IsModified = true;

        bool headerFound = TrwContainer.MatchesMagic(_data, sector.Start);
        long? actualCpu = headerFound ? TrwContainer.CpuOffsetAt(_data, sector.Start) : null;

        if (repairCrc && headerFound)
        {
            long cpuOffset = actualCpu ?? sector.CpuOffset;
            long endAddr = ByteOrder.ReadUInt32(_data, sector.Start + FlashFormat.EndAddrOffset,
                                                Endianness.Big);
            long length = FlashFormat.SectorLength(endAddr, cpuOffset);
            if (length > FlashFormat.MinSectorLength && sector.Start + length <= _data.LongLength)
                ByteOrder.WriteUInt32(_data, sector.Start + length - FlashFormat.CrcTrailerLen,
                                      TrwContainer.SectorCrc(_data, sector.Start, length),
                                      Endianness.Big);
        }

        return new SectorReplacement(headerFound, sector.CpuOffset, actualCpu);
    }

    // ------------------------------------------------------------------
    // Blockübertrag aus einem zweiten Abbild
    // ------------------------------------------------------------------

    /// <summary>
    /// Übernimmt einen Block aus einem anderen Abbild an <em>dieselbe</em>
    /// CPU-Adresse. Geschrieben werden ausschließlich die Bytes der Quelle und —
    /// wenn sie kürzer ist — das Füllmuster bis zum bisherigen Blockende.
    ///
    /// Was hier ausdrücklich <em>nicht</em> geschieht: kein Adressfeld wird
    /// umgeschrieben, keine Prüfsumme gestellt, keine CVN nachgezogen, keine
    /// Prüfwertkopie mitgeführt. Der Sinn der 1:1-Bedingung ist gerade, dass
    /// nichts davon nötig ist — <c>blockEnd</c>, <c>nextSector</c> und die
    /// Tabellenzeiger im übernommenen Kopf stehen absolut im Adressraum und
    /// bleiben genau deshalb gültig.
    ///
    /// Anders als <see cref="RepairCrc"/> ruft dieser Vorgang
    /// <see cref="Analyze"/> selbst: das Ergebnis <em>ist</em> die Aussage
    /// darüber, was danach noch aufgeht, und ohne Neuanalyse gäbe es sie nicht.
    /// </summary>
    public BlockTransferResult CopyBlockFrom(BlockTransferPlan plan, bool fixedAddressesOnly = false)
    {
        EnsureBlockTransfer();

        if (!ReferenceEquals(plan.Target, this))
            throw new ArgumentException("Der Plan gehört zu einem anderen Zielabbild.", nameof(plan));

        if (plan.TargetBlock is not { } target || !plan.Possible)
            throw new InvalidOperationException("Der Blockübertrag wurde abgelehnt: " +
                                                plan.RejectionText);

        // Die 1:1-Bedingung ein zweites Mal, hier im Schreibpfad. Wer freies
        // Platzieren einbauen will, muss diesen Satz löschen — und das steht
        // dann im Diff.
        if (PhysicalLayout.Normalize(target.CpuOffset) !=
            PhysicalLayout.Normalize(plan.SourceBlock.CpuOffset))
            throw new InvalidOperationException(
                "Ein Blockübertrag geht nur an dieselbe CPU-Adresse. Nur so bleiben blockEnd, " +
                "nextSector und die Tabellenzeiger im übernommenen Kopf gültig — sie stehen " +
                "absolut im Adressraum und werden nicht umgeschrieben.");

        byte[] bytes = plan.Source.SectorBytes(plan.SourceBlock);

        if (target.Start + bytes.LongLength > _data.LongLength)
            throw new InvalidOperationException(
                $"Der Block reicht über das Dateiende hinaus: {bytes.Length:N0} B ab " +
                $"{Hex.Addr(target.Start)}, die Datei hat {_data.LongLength:N0} B.");

        var before = ChecksumSnapshot();
        uint? cvnBefore = Chain.Cvn?.Value;

        bytes.CopyTo(_data, (int)target.Start);

        long tail = 0;
        for (long i = target.Start + bytes.LongLength; i < target.End; i++, tail++)
            _data[i] = 0xFF;

        IsModified = true;
        Analyze(fixedAddressesOnly);

        var copied = Sectors.FirstOrDefault(s => s.Start == target.Start);
        bool valid = copied is { Present: true };

        var findings = new List<TransferFinding>(plan.Findings);
        AppendChainFinding(findings, target.Start);

        return new BlockTransferResult(
            plan, bytes.LongLength, tail, valid,
            copied?.Status ?? SectorStatus.Missing,
            CompareChecksums(before, ChecksumSnapshot()),
            findings, cvnBefore, Chain.Cvn?.Value);
    }

    /// <summary>
    /// Ergebnis jeder Prüfsummenstruktur des Abbilds, nach Block und Stelle.
    /// Dreiwertig wie <c>Ok</c> selbst: null heißt „nicht nachgerechnet".
    /// </summary>
    private Dictionary<(long Block, int Index), bool?> ChecksumSnapshot() =>
        Chain.Blocks
             .SelectMany(b => b.Checksums.Select((c, i) => (Key: (b.FileStart, i), c.Ok)))
             .ToDictionary(x => x.Key, x => x.Ok);

    /// <summary>
    /// Was sich zwischen den beiden Momentaufnahmen geändert hat — über das
    /// ganze Abbild, nicht nur über den übernommenen Block. Eine Struktur eines
    /// anderen Blocks, deren geprüfter Bereich in den beschriebenen Teil
    /// hineinreicht, geht danach nicht mehr auf, und das ist gemessen.
    /// </summary>
    private List<ChecksumChange> CompareChecksums(
        Dictionary<(long Block, int Index), bool?> before,
        Dictionary<(long Block, int Index), bool?> after)
    {
        var changes = new List<ChecksumChange>();

        foreach (var key in before.Keys.Union(after.Keys))
        {
            before.TryGetValue(key, out bool? was);
            after.TryGetValue(key, out bool? now);
            if (was == now) continue;

            string label = Chain.Blocks.FirstOrDefault(b => b.FileStart == key.Block)?.IdName
                           ?? Hex.Addr(key.Block);

            changes.Add(new ChecksumChange(key.Block, label, key.Index, was, now));
        }

        return changes;
    }

    /// <summary>
    /// Der übernommene <c>nextSector</c> nennt den Nachfolger aus dem
    /// Quellabbild. Liegt dort im Ziel kein Blockkopf, ist die Kette an dieser
    /// Stelle umgeleitet — feststellbar erst nach dem Schreiben.
    /// </summary>
    private void AppendChainFinding(List<TransferFinding> findings, long fileStart)
    {
        if (Chain.Blocks.FirstOrDefault(b => b.FileStart == fileStart) is not { } block) return;
        if (block.NextCpu is not { } next) return;
        if (Chain.Blocks.Any(b => PhysicalLayout.Normalize(b.CpuStart) ==
                                  PhysicalLayout.Normalize(next))) return;

        findings.Add(new TransferFinding(TransferRule.NextSectorLeavesChain,
            TransferSeverity.Warning,
            $"Der übernommene Kopf nennt als nächsten Block {Hex.Addr(next)}; dort liegt im " +
            "Ziel kein Blockkopf. Die Kette ist an dieser Stelle umgeleitet — sie wird " +
            "gelesen und ausgewiesen, nicht ausgebessert."));
    }

    /// <summary>Platz vom Sektoranfang bis zum nächsten Sektor bzw. Dateiende.</summary>
    public long AvailableSpace(SectorInfo sector)
    {
        long next = _data.LongLength;
        foreach (var other in Sectors)
            if (other.Present && other.Start > sector.Start && other.Start < next)
                next = other.Start;
        return next - sector.Start;
    }

    public void Save(string path)
    {
        EnsureSaveable();
        File.WriteAllBytes(path, _data);
        IsModified = false;
    }
}
