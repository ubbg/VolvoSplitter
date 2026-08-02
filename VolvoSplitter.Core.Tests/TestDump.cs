using System.Text;
using VolvoSplitter.Core;

namespace VolvoSplitter.Core.Tests;

/// <summary>
/// Baut synthetische Flash-Abbilder, die genau dem Format entsprechen, das
/// <see cref="FlashDump"/> erwartet — Sektorkopf, Endadresse und korrekte CRC.
/// Damit lassen sich Erkennung und Prüfsummenlogik ohne echte Dumps testen.
/// </summary>
public static class TestDump
{
    /// <summary>
    /// Baut einen einzelnen Sektor der gewünschten Gesamtlänge mit gültigem Kopf,
    /// passender Endadresse und korrekt berechneter CRC im Trailer.
    /// </summary>
    public static byte[] Sector(long cpuOffset, string partNumber, int totalLength,
                                string extraFields = "f=x.dst2;", byte fill = 0xAB,
                                Action<byte[]>? patchBody = null)
    {
        if (partNumber.Length != FlashFormat.VersionStringLen)
            throw new ArgumentException($"Teilenummer muss {FlashFormat.VersionStringLen} Zeichen haben.");
        if (totalLength <= FlashFormat.MinSectorLength)
            throw new ArgumentException("Sektor zu kurz.");

        var data = new byte[totalLength];

        // Kopf: "v=1;a=<TeileNr>;o=<CPU-Offset>;<weitere Felder>"
        string header = $"v=1;a={partNumber};o={cpuOffset:X6};{extraFields}";
        Encoding.ASCII.GetBytes(header).CopyTo(data, 0);
        data[header.Length] = 0x00;   // Terminator für ParseHeaderFields

        // Nutzdaten von der Endadresse bis vor den Trailer
        for (int i = FlashFormat.EndAddrOffset; i < totalLength - FlashFormat.CrcTrailerLen; i++)
            data[i] = fill;

        // Endadresse (big endian) bei 0x0F8 — sodass Länge = endAddr - cpuOffset + 44
        long endAddr = cpuOffset + totalLength - FlashFormat.CrcOffset;
        WriteBe(data, FlashFormat.EndAddrOffset, (uint)endAddr);

        // Vor der CRC-Berechnung darf der Aufrufer noch Nutzdaten setzen
        // (z. B. Fahrzeugkennung), damit sie von der Prüfsumme abgedeckt sind.
        patchBody?.Invoke(data);

        // CRC über [0xF8 .. Ende-4], in den Trailer
        uint crc = Crc32.Compute(data.AsSpan(FlashFormat.EndAddrOffset,
            totalLength - FlashFormat.EndAddrOffset - FlashFormat.CrcTrailerLen));
        WriteBe(data, totalLength - FlashFormat.CrcTrailerLen, crc);

        return data;
    }

    /// <summary>Legt einen Sektor an einer Adresse in einem leeren (0xFF) Abbild ab.</summary>
    public static byte[] Image(long imageSize, params (long Start, byte[] Bytes)[] sectors)
    {
        var data = new byte[imageSize];
        Array.Fill(data, (byte)0xFF);
        foreach (var (start, bytes) in sectors)
            bytes.CopyTo(data, (int)start);
        return data;
    }

    public static void WriteBe(byte[] data, long offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    public static uint ReadBe(ReadOnlySpan<byte> data, int offset) =>
        (uint)((data[offset] << 24) | (data[offset + 1] << 16) |
               (data[offset + 2] << 8) | data[offset + 3]);

    /// <summary>Eindeutiger Pfad im Temp-Verzeichnis für Datei-basierte Tests.</summary>
    public static string TempPath(string extension = ".tmp") =>
        Path.Combine(Path.GetTempPath(), "vs_test_" + Guid.NewGuid().ToString("N") + extension);
}
