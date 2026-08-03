using System.Text;

namespace VolvoSplitter.Core;

/// <summary>
/// Ein ausführbares VOLVOECU-Modul im Low-Flash. Eigenes Format, nicht zu
/// verwechseln mit den Sektoren des Large Flash („v=1;a=…") und erst recht
/// nicht mit den EEPROM-Emulationsdaten:
///
///     +0x000  ASCII „VOLVOECU"                        Magic
///     +0x008  Startadresse des Codes (4 B, big endian, CPU-Adressraum)
///     +0x00C  Adresse des CRC-Trailers (4 B, big endian, CPU-Adressraum)
///     +0x400  Beginn des PowerPC/VLE-Codes
///     +0x4F0  Teilenummer (11 Zeichen)
///     CRC-Pos CRC32 (4 B, big endian) über den Block vom Anfang bis hierher
///
/// Anders als beim Sektorformat läuft die Prüfsumme über den <em>gesamten</em>
/// Block einschließlich Kopf, nicht erst ab 0x0F8.
/// </summary>
public sealed record VolvoEcuBlock(long FileStart, long CpuStart, long CodeStart, long CrcAddress,
                                   long Length, string PartNumber, uint CrcStored, uint CrcComputed)
{
    /// <summary>Kopf bis zum Codebeginn.</summary>
    public const int HeaderLength = 0x400;

    public const int CodeStartOffset = 0x008;
    public const int CrcAddressOffset = 0x00C;
    public const int PartNumberOffset = 0x4F0;

    public static readonly byte[] Magic = "VOLVOECU"u8.ToArray();

    public long FileEnd => FileStart + Length;
    public bool CrcOk => CrcStored == CrcComputed;

    /// <summary>
    /// Liest einen VOLVOECU-Block an dieser Stelle. Die CPU-Basis stammt aus dem
    /// Block selbst — der Code beginnt stets 0x400 hinter dem Kopf, also ist
    /// <c>CpuStart = CodeStart - 0x400</c>. Damit lässt sich die Containerabbildung
    /// unabhängig gegenprüfen: Der Wert muss dem CPU-Anfang der Partition
    /// entsprechen, in der der Block liegt.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> data, long fileStart, out VolvoEcuBlock? block)
    {
        block = null;

        if (fileStart < 0 || fileStart + HeaderLength > data.Length) return false;

        var head = data.Slice((int)fileStart, HeaderLength);
        if (!head[..Magic.Length].SequenceEqual(Magic)) return false;

        long codeStart = ReadBe32(head[CodeStartOffset..]);
        long crcAddress = ReadBe32(head[CrcAddressOffset..]);

        long cpuStart = codeStart - HeaderLength;
        if (cpuStart < 0 || crcAddress <= codeStart) return false;

        // Relativ rechnen, damit die Datei-Offsets unabhängig von der CPU-Basis bleiben.
        long crcOffset = crcAddress - cpuStart;
        long length = crcOffset + FlashFormat.CrcTrailerLen;
        if (length <= HeaderLength || fileStart + length > data.Length) return false;

        uint stored = ReadBe32(data[(int)(fileStart + crcOffset)..]);
        uint computed = Crc32.Compute(data.Slice((int)fileStart, (int)crcOffset));

        block = new VolvoEcuBlock(fileStart, cpuStart, codeStart, crcAddress, length,
                                  ReadPartNumber(data, (int)fileStart + PartNumberOffset),
                                  stored, computed);
        return true;
    }

    private static string ReadPartNumber(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset + FlashFormat.VersionStringLen > data.Length) return "";

        string text = Encoding.ASCII.GetString(data.Slice(offset, FlashFormat.VersionStringLen));
        return text.Any(char.IsControl) ? "" : text.Trim();
    }

    private static uint ReadBe32(ReadOnlySpan<byte> data) =>
        (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);
}
