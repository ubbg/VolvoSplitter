using System.IO;
using System.Text;

namespace VolvoSplitter.Core;

/// <summary>Fahrzeugdaten aus dem Parameter-Sektor.</summary>
public sealed record VehicleInfo(string Vin, string ChassisNumber, string Maker);

/// <summary>
/// Ein geladenes Flash-Abbild. Hält eine Arbeitskopie im Speicher, damit
/// Sektoren ersetzt und Prüfsummen korrigiert werden können, ohne die
/// Originaldatei anzufassen.
/// </summary>
public sealed class FlashDump
{
    private byte[] _data;

    private FlashDump(string path, byte[] data)
    {
        SourcePath = path;
        _data = data;
        Family = FlashFormat.DetectFamily(data.LongLength);
        Sectors = [];
    }

    public string SourcePath { get; }
    public string FileName => Path.GetFileName(SourcePath);
    public string Directory => Path.GetDirectoryName(Path.GetFullPath(SourcePath)) ?? ".";
    public long Size => _data.LongLength;
    public EcuFamily Family { get; }

    /// <summary>Arbeitskopie wurde verändert und ist noch nicht gespeichert.</summary>
    public bool IsModified { get; private set; }

    public IReadOnlyList<SectorInfo> Sectors { get; private set; }

    /// <summary>Belegte Bereiche ohne Sektorkopf — Programmcode, Chiffretext, EEPROM.</summary>
    public IReadOnlyList<FlashRegion> Regions { get; private set; } = [];

    /// <summary>Größe des Flash-Bausteins; alles dahinter ist angehängt.</summary>
    public long FlashSize => FlashFormat.FlashSizeFor(Family);
    public VehicleInfo? Vehicle { get; private set; }

    /// <summary>Begleitende Protokolldatei des Auslesegeräts, falls vorhanden.</summary>
    public EcuReport? Report { get; private set; }

    public ReadOnlySpan<byte> Raw => _data;

    public static FlashDump Load(string path, bool fixedAddressesOnly = false)
    {
        var dump = new FlashDump(path, File.ReadAllBytes(path));
        dump.Report = EcuReport.FindFor(path);
        dump.Analyze(fixedAddressesOnly);
        return dump;
    }

    /// <summary>
    /// Baut ein Abbild aus bereits im Speicher liegenden Bytes. Der Name dient
    /// nur der Benennung; eine begleitende Protokolldatei wird nicht gesucht.
    /// Für Aufrufer, die die Daten schon halten, und für Tests.
    /// </summary>
    public static FlashDump FromBytes(byte[] data, string name = "memory.mpc",
                                      bool fixedAddressesOnly = false)
    {
        var dump = new FlashDump(name, data);
        dump.Analyze(fixedAddressesOnly);
        return dump;
    }

    // ------------------------------------------------------------------
    // Analyse
    // ------------------------------------------------------------------

    /// <param name="fixedAddressesOnly">
    /// Nur die fest verdrahteten Adressen lesen — das Verhalten der V2. Sonst
    /// wird das ganze Abbild durchsucht, sodass auch verschobene Blöcke
    /// gefunden werden.
    /// </param>
    public void Analyze(bool fixedAddressesOnly = false)
    {
        var slots = fixedAddressesOnly
            ? FlashFormat.SlotsFor(Family).ToList()
            : DiscoverSlots();

        var sectors = new List<SectorInfo>();
        foreach (var slot in slots.OrderBy(s => s.Start))
            sectors.Add(ReadSector(slot));

        Sectors = sectors;
        Vehicle = ReadVehicleInfo();
        Regions = RegionScanner.Scan(_data, FlashSize, sectors);

        foreach (var sector in sectors)
        {
            if (sector.Present)
                sector.ChecksumCopies = FindChecksumCopies(sector);
            else
                sector.MissingReason = ExplainMissing(sector);
        }
    }

    /// <summary>
    /// Sagt, was an der Adresse eines fehlenden Sektors tatsächlich liegt,
    /// statt pauschal "leer oder verschlüsselt" zu melden.
    /// </summary>
    private string ExplainMissing(SectorInfo sector)
    {
        if (sector.Start + FlashFormat.MinSectorLength > _data.LongLength)
            return $"Adresse 0x{sector.Start:X6} liegt außerhalb der Datei";

        var region = Regions.FirstOrDefault(r => sector.Start >= r.Start && sector.Start < r.End);

        if (region is null)
            return $"Kein Sektorkopf bei 0x{sector.Start:X6} — Bereich ist gelöscht (0xFF)";

        string what = region.Kind switch
        {
            RegionKind.Code => "dort steht Programmcode im Klartext, aber ohne Sektorkopf",
            RegionKind.Opaque => "dort steht verschlüsselter oder signierter Inhalt",
            RegionKind.Eeprom => "dort beginnt der angehängte EEPROM-Auszug",
            _ => "dort stehen Daten ohne Sektorkopf"
        };

        return $"Kein Sektorkopf bei 0x{sector.Start:X6} — {what} " +
               $"({region.AddressRange}, {region.SizeText})";
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
    public IReadOnlyList<long> FindUInt32Be(uint value, long skipFrom = -1, long skipTo = -1)
    {
        var pattern = new[]
        {
            (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
        };

        var hits = new List<long>();
        for (int i = 0; i + 4 <= _data.Length; i++)
        {
            if (_data[i] != pattern[0] || _data[i + 1] != pattern[1] ||
                _data[i + 2] != pattern[2] || _data[i + 3] != pattern[3]) continue;
            if (i >= skipFrom && i < skipTo) continue;
            hits.Add(i);
        }
        return hits;
    }

    /// <summary>
    /// Sucht alle Sektorköpfe im gesamten Abbild und ordnet sie den bekannten
    /// Rollen zu. Ein Block wird also auch dann als Parameter- oder
    /// Kalibrierungssektor erkannt, wenn er nicht an der erwarteten Adresse
    /// liegt — die feste Tabelle dient dann nur noch der Benennung.
    /// </summary>
    private List<SectorSlot> DiscoverSlots()
    {
        var known = FlashFormat.SlotsFor(Family);
        var slots = new List<SectorSlot>();
        var taken = new HashSet<SectorKind>();

        foreach (var (pos, cpuOffset, fields) in FindHeaders())
        {
            var slot = MatchSlot(known, taken, pos, cpuOffset, fields);
            if (slot.Kind != SectorKind.Other) taken.Add(slot.Kind);
            slots.Add(slot);
        }

        // Rollen, für die nirgends ein Block auftauchte, trotzdem melden —
        // sonst verschwindet ein fehlender ASW-Sektor stillschweigend.
        foreach (var slot in known)
            if (!taken.Contains(slot.Kind))
                slots.Add(slot);

        return slots;
    }

    /// <summary>Alle Sektorköpfe im Abbild, in Adressreihenfolge.</summary>
    private IEnumerable<(long Position, long CpuOffset, Dictionary<string, string> Fields)> FindHeaders()
    {
        var magic = FlashFormat.HeaderMagic;

        for (int pos = 0; pos + magic.Length < _data.Length; pos++)
        {
            if (_data[pos] != magic[0]) continue;
            if (!MatchesMagic(pos)) continue;

            var fields = ParseHeaderFields(pos);
            if (!fields.TryGetValue("o", out var offsetText)) continue;
            if (!long.TryParse(offsetText, System.Globalization.NumberStyles.HexNumber,
                               null, out long cpuOffset)) continue;

            yield return (pos, cpuOffset, fields);
        }
    }

    /// <summary>Steht an dieser Stelle der Kopfbeginn "v=1;a="?</summary>
    private bool MatchesMagic(int pos) =>
        _data.AsSpan(pos, FlashFormat.HeaderMagic.Length).SequenceEqual(FlashFormat.HeaderMagic);

    /// <summary>
    /// Ordnet einen gefundenen Block einer Rolle zu — nach Adresse, sonst nach
    /// CPU-Offset, sonst nach der Datensatzkennung im Dateinamen des Kopfes.
    /// </summary>
    private static SectorSlot MatchSlot(IReadOnlyList<SectorSlot> known, HashSet<SectorKind> taken,
                                        long pos, long cpuOffset,
                                        IReadOnlyDictionary<string, string> fields)
    {
        SectorSlot? Free(Func<SectorSlot, bool> predicate) =>
            known.FirstOrDefault(s => !taken.Contains(s.Kind) && predicate(s));

        // 1. Der Block liegt genau dort, wo er hingehört.
        var match = Free(s => s.Start == pos)
                    // 2. Er nennt selbst den CPU-Offset einer bekannten Rolle.
                    ?? Free(s => s.CpuOffset == cpuOffset)
                    // 3. Die Datensatzkennung verrät, was er ist.
                    ?? Free(s => s.Kind == KindFromHeader(fields));

        // Rolle übernehmen, aber die tatsächliche Lage aus dem Abbild.
        if (match is not null)
            return match with
            {
                Start = pos,
                CpuOffset = cpuOffset,
                ExpectedStart = match.Start == pos ? null : match.Start
            };

        return new SectorSlot(SectorKind.Other, LabelFromHeader(fields), "sec_", pos, cpuOffset);
    }

    private static SectorKind KindFromHeader(IReadOnlyDictionary<string, string> fields)
    {
        fields.TryGetValue("f", out var file);
        file ??= "";
        if (file.Contains(".dst2")) return SectorKind.Parameter;
        if (file.Contains(".dst1")) return SectorKind.Calibration;
        return SectorKind.Other;
    }

    private static string LabelFromHeader(IReadOnlyDictionary<string, string> fields)
    {
        fields.TryGetValue("f", out var file);
        file ??= "";
        if (file.Contains(".pbc")) return "PBC-Block";
        if (file.Contains(".dst1")) return "Datensatz 1";
        if (file.Contains(".dst2")) return "Datensatz 2";
        return "Sektor";
    }

    private SectorInfo ReadSector(SectorSlot slot)
    {
        SectorInfo Missing(string reason) => new()
        {
            Kind = slot.Kind,
            Label = slot.Label,
            Prefix = slot.Prefix,
            Start = slot.Start,
            CpuOffset = slot.CpuOffset,
            Status = SectorStatus.Missing,
            MissingReason = reason
        };

        if (slot.Start + FlashFormat.MinSectorLength > _data.LongLength)
            return Missing($"Adresse 0x{slot.Start:X6} liegt außerhalb der Datei");

        if (!_data.AsSpan((int)slot.Start, FlashFormat.HeaderMagic.Length)
                  .SequenceEqual(FlashFormat.HeaderMagic))
            return Missing($"Kein Sektorkopf bei 0x{slot.Start:X6}");

        string partNumber = Ascii(slot.Start + FlashFormat.VersionPosOffset,
                                  FlashFormat.VersionStringLen);
        if (partNumber.Length != FlashFormat.VersionStringLen || partNumber.Any(char.IsControl))
            return Missing($"Teilenummer bei 0x{slot.Start:X6} ist unlesbar");

        long endAddr = ReadUInt32Be(slot.Start + FlashFormat.EndAddrOffset);
        long length = FlashFormat.SectorLength(endAddr, slot.CpuOffset);

        if (length <= FlashFormat.MinSectorLength)
            return Missing($"Unplausible Länge (Endadresse 0x{endAddr:X8}, CPU-Offset 0x{slot.CpuOffset:X8})");

        bool truncated = false;
        if (slot.Start + length > _data.LongLength)
        {
            length = _data.LongLength - slot.Start;
            truncated = true;
        }

        uint stored = ReadUInt32Be(slot.Start + length - FlashFormat.CrcTrailerLen);
        uint computed = ComputeSectorCrc(slot.Start, length);

        var fields = ParseHeaderFields(slot.Start);
        fields.TryGetValue("p", out var project);
        fields.TryGetValue("d", out var date);
        fields.TryGetValue("t", out var time);
        fields.TryGetValue("f", out var sourceFile);
        fields.TryGetValue("b", out var baseline);

        return new SectorInfo
        {
            Kind = slot.Kind,
            Label = slot.Label,
            Prefix = slot.Prefix,
            Start = slot.Start,
            CpuOffset = slot.CpuOffset,
            Status = stored == computed ? SectorStatus.Verified : SectorStatus.CrcMismatch,
            ExpectedStart = slot.ExpectedStart,
            PartNumber = partNumber,
            Length = length,
            CrcStored = stored,
            CrcComputed = computed,
            Truncated = truncated,
            Project = project ?? "",
            BuildDate = date ?? "",
            BuildTime = time ?? "",
            SourceFile = sourceFile ?? "",
            Baseline = baseline ?? "",
            HeaderFields = fields
        };
    }

    private VehicleInfo? ReadVehicleInfo()
    {
        var param = Sectors.FirstOrDefault(s => s.Kind == SectorKind.Parameter && s.Present);
        if (param is null || param.Length < FlashFormat.VinOffset + FlashFormat.VinLength)
            return null;

        string maker = Ascii(param.Start + FlashFormat.MakerOffset, FlashFormat.MakerLength);
        if (maker != "VOLVO")
            return null;

        string chassis = Ascii(param.Start + FlashFormat.ChassisOffset, FlashFormat.ChassisLength);
        string vin = Ascii(param.Start + FlashFormat.VinOffset, FlashFormat.VinLength);

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
        long crcPos = sector.Start + sector.Length - FlashFormat.CrcTrailerLen;
        uint before = ReadUInt32Be(crcPos);

        uint crc = ComputeSectorCrc(sector.Start, sector.Length);
        WriteUInt32Be(crcPos, crc);
        IsModified = true;

        var stale = before == crc
            ? []
            : FindUInt32Be(before, skipFrom: crcPos, skipTo: crcPos + FlashFormat.CrcTrailerLen);

        return new CrcRepair(before, crc, stale);
    }

    /// <summary>Trägt einen Prüfwert an einer beliebigen Stelle ein.</summary>
    public void PatchUInt32Be(long offset, uint value)
    {
        WriteUInt32Be(offset, value);
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

        bool headerFound = _data.AsSpan((int)sector.Start, FlashFormat.HeaderMagic.Length)
                                 .SequenceEqual(FlashFormat.HeaderMagic);
        long? actualCpu = headerFound ? ReadCpuOffset(sector.Start) : null;

        if (repairCrc && headerFound)
        {
            long cpuOffset = actualCpu ?? sector.CpuOffset;
            long endAddr = ReadUInt32Be(sector.Start + FlashFormat.EndAddrOffset);
            long length = FlashFormat.SectorLength(endAddr, cpuOffset);
            if (length > FlashFormat.MinSectorLength && sector.Start + length <= _data.LongLength)
                WriteUInt32Be(sector.Start + length - FlashFormat.CrcTrailerLen,
                              ComputeSectorCrc(sector.Start, length));
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
        File.WriteAllBytes(path, _data);
        IsModified = false;
    }

    // ------------------------------------------------------------------
    // Hilfsfunktionen
    // ------------------------------------------------------------------

    private Dictionary<string, string> ParseHeaderFields(long start)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        int max = (int)Math.Min(256, _data.LongLength - start);
        if (max <= 0) return fields;

        var span = _data.AsSpan((int)start, max);
        int end = span.IndexOfAny((byte)0x00, (byte)0xFF);
        if (end >= 0) span = span[..end];

        foreach (var part in Encoding.ASCII.GetString(span).Split(';'))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            fields[part[..eq]] = part[(eq + 1)..];
        }
        return fields;
    }

    private string Ascii(long offset, int length)
    {
        if (offset < 0 || offset + length > _data.LongLength) return "";
        return Encoding.ASCII.GetString(_data, (int)offset, length);
    }

    /// <summary>
    /// CRC32 eines Sektors: über die Nutzdaten von der Endadresse bis vor den
    /// Trailer. Eine Stelle für die Bereichsdefinition, die sonst mehrfach
    /// gepflegt werden müsste.
    /// </summary>
    private uint ComputeSectorCrc(long start, long length)
    {
        var body = _data.AsSpan((int)start, (int)length);
        return Crc32.Compute(body[FlashFormat.EndAddrOffset..^FlashFormat.CrcTrailerLen]);
    }

    /// <summary>CPU-Offset (o=) aus dem Sektorkopf an dieser Adresse, falls lesbar.</summary>
    private long? ReadCpuOffset(long start)
    {
        if (ParseHeaderFields(start).TryGetValue("o", out var text) &&
            long.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out long value))
            return value;
        return null;
    }

    private uint ReadUInt32Be(long offset) =>
        (uint)((_data[offset] << 24) | (_data[offset + 1] << 16) |
               (_data[offset + 2] << 8) | _data[offset + 3]);

    private void WriteUInt32Be(long offset, uint value)
    {
        _data[offset] = (byte)(value >> 24);
        _data[offset + 1] = (byte)(value >> 16);
        _data[offset + 2] = (byte)(value >> 8);
        _data[offset + 3] = (byte)value;
    }
}
