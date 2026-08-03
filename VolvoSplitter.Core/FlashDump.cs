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
    /// Hand vorgegeben wurde.
    /// </summary>
    private List<SectorInfo> ReadBoschBlocks()
    {
        if (Layout is null) return [];

        Chain = Detection.Chain.Blocks.Count > 0
            ? Detection.Chain
            : BoschBlockChain.Read(_data, Layout);

        Identity = Detection.Identity
                   ?? BoschIdentity.Scan(_data, Chain.Variant, Chain.VariantOffset ?? 0);

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
                                   .Select(s => $"Sektor {s.Label}"));
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
        EnsureWriteBack();
        File.WriteAllBytes(path, _data);
        IsModified = false;
    }
}
