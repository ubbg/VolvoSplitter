using System.Globalization;
using System.Text;

namespace VolvoSplitter.Core;

/// <summary>
/// Das TRW-Sektorformat: Sektorköpfe finden, ihnen Rollen zuordnen und den
/// einzelnen Sektor samt Prüfsumme lesen. Aus <see cref="FlashDump"/>
/// herausgelöst, damit dort nur noch Laden, Orchestrierung, Änderung und
/// Ausgabe stehen — und damit ein TriCore-Abbild gar nicht erst durch diesen
/// Code läuft.
///
/// Der Aufbau selbst ist in <see cref="FlashFormat"/> beschrieben.
/// </summary>
public static class TrwContainer
{
    /// <summary>
    /// Sucht alle Sektorköpfe im gesamten Abbild und ordnet sie den bekannten
    /// Rollen zu. Ein Block wird also auch dann als Parameter- oder
    /// Kalibrierungssektor erkannt, wenn er nicht an der erwarteten Adresse
    /// liegt — die feste Tabelle dient dann nur noch der Benennung.
    /// </summary>
    public static List<SectorSlot> DiscoverSlots(byte[] data, IReadOnlyList<SectorSlot> known)
    {
        var slots = new List<SectorSlot>();
        var taken = new HashSet<SectorKind>();

        foreach (var (pos, cpuOffset, fields) in FindHeaders(data))
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
    public static IEnumerable<(long Position, long CpuOffset, Dictionary<string, string> Fields)>
        FindHeaders(byte[] data)
    {
        var magic = FlashFormat.HeaderMagic;

        for (int pos = 0; pos + magic.Length < data.Length; pos++)
        {
            if (data[pos] != magic[0]) continue;
            if (!MatchesMagic(data, pos)) continue;

            var fields = HeaderFields(data, pos);
            if (!fields.TryGetValue("o", out var offsetText)) continue;
            if (!long.TryParse(offsetText, NumberStyles.HexNumber, null, out long cpuOffset)) continue;

            yield return (pos, cpuOffset, fields);
        }
    }

    /// <summary>Steht an dieser Stelle der Kopfbeginn "v=1;a="?</summary>
    public static bool MatchesMagic(byte[] data, long pos) =>
        pos >= 0 && pos + FlashFormat.HeaderMagic.Length <= data.LongLength &&
        data.AsSpan((int)pos, FlashFormat.HeaderMagic.Length).SequenceEqual(FlashFormat.HeaderMagic);

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

    public static SectorKind KindFromHeader(IReadOnlyDictionary<string, string> fields)
    {
        fields.TryGetValue("f", out var file);
        file ??= "";
        if (file.Contains(".dst2")) return SectorKind.Parameter;
        if (file.Contains(".dst1")) return SectorKind.Calibration;
        return SectorKind.Other;
    }

    public static string LabelFromHeader(IReadOnlyDictionary<string, string> fields)
    {
        fields.TryGetValue("f", out var file);
        file ??= "";
        if (file.Contains(".pbc")) return "PBC-Block";
        if (file.Contains(".dst1")) return "Datensatz 1";
        if (file.Contains(".dst2")) return "Datensatz 2";
        return "Sektor";
    }

    public static SectorInfo ReadSector(byte[] data, SectorSlot slot)
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

        if (slot.Start + FlashFormat.MinSectorLength > data.LongLength)
            return Missing($"Adresse {Hex.Addr(slot.Start)} liegt außerhalb der Datei");

        if (!MatchesMagic(data, slot.Start))
            return Missing($"Kein Sektorkopf bei {Hex.Addr(slot.Start)}");

        string partNumber = Ascii(data, slot.Start + FlashFormat.VersionPosOffset,
                                  FlashFormat.VersionStringLen);
        if (partNumber.Length != FlashFormat.VersionStringLen || partNumber.Any(char.IsControl))
            return Missing($"Teilenummer bei {Hex.Addr(slot.Start)} ist unlesbar");

        long endAddr = ByteOrder.ReadUInt32(data, slot.Start + FlashFormat.EndAddrOffset, Endianness.Big);
        long length = FlashFormat.SectorLength(endAddr, slot.CpuOffset);

        if (length <= FlashFormat.MinSectorLength)
            return Missing($"Unplausible Länge (Endadresse 0x{endAddr:X8}, " +
                           $"CPU-Offset 0x{slot.CpuOffset:X8})");

        bool truncated = false;
        if (slot.Start + length > data.LongLength)
        {
            length = data.LongLength - slot.Start;
            truncated = true;
        }

        uint stored = ByteOrder.ReadUInt32(data, slot.Start + length - FlashFormat.CrcTrailerLen,
                                           Endianness.Big);
        uint computed = SectorCrc(data, slot.Start, length);

        var fields = HeaderFields(data, slot.Start);
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

    /// <summary>
    /// CRC32 eines Sektors: über die Nutzdaten von der Endadresse bis vor den
    /// Trailer. Eine Stelle für die Bereichsdefinition, die sonst mehrfach
    /// gepflegt werden müsste.
    /// </summary>
    public static uint SectorCrc(byte[] data, long start, long length)
    {
        var body = data.AsSpan((int)start, (int)length);
        return Crc32.Compute(body[FlashFormat.EndAddrOffset..^FlashFormat.CrcTrailerLen]);
    }

    /// <summary>CPU-Offset (o=) aus dem Sektorkopf an dieser Adresse, falls lesbar.</summary>
    public static long? CpuOffsetAt(byte[] data, long start) =>
        HeaderFields(data, start).TryGetValue("o", out var text) &&
        long.TryParse(text, NumberStyles.HexNumber, null, out long value)
            ? value
            : null;

    public static Dictionary<string, string> HeaderFields(byte[] data, long start)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        int max = (int)Math.Min(256, data.LongLength - start);
        if (max <= 0) return fields;

        var span = data.AsSpan((int)start, max);
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

    public static string Ascii(byte[] data, long offset, int length)
    {
        if (offset < 0 || offset + length > data.LongLength) return "";
        return Encoding.ASCII.GetString(data, (int)offset, length);
    }
}
