using System.IO;
using System.Text;

namespace VolvoSplitter.Core;

/// <summary>
/// Motorola-S-Record-Ausgabe (S3, 32-Bit-Adressen) — das Format, in dem die
/// Original-Datensätze ausgeliefert werden (siehe "f=...s3" im Sektorkopf).
/// </summary>
public static class SRecord
{
    private const int BytesPerLine = 32;

    public static void Write(string path, ReadOnlySpan<byte> data, long baseAddress, string name)
    {
        var text = new StringBuilder();

        AppendHeader(text, name);

        long recordCount = 0;
        for (int offset = 0; offset < data.Length; offset += BytesPerLine)
        {
            int length = Math.Min(BytesPerLine, data.Length - offset);
            AppendData(text, baseAddress + offset, data.Slice(offset, length));
            recordCount++;
        }

        AppendCount(text, recordCount);
        AppendTermination(text, baseAddress);

        File.WriteAllText(path, text.ToString(), Encoding.ASCII);
    }

    /// <summary>S0 — Kopfsatz mit dem Namen des Datensatzes.</summary>
    private static void AppendHeader(StringBuilder text, string name)
    {
        var payload = new List<byte> { 0x00, 0x00 };
        payload.AddRange(Encoding.ASCII.GetBytes(name.Length > 32 ? name[..32] : name));
        AppendRecord(text, "S0", payload);
    }

    /// <summary>S3 — Datensatz mit 32-Bit-Adresse.</summary>
    private static void AppendData(StringBuilder text, long address, ReadOnlySpan<byte> chunk)
    {
        var payload = new List<byte>
        {
            (byte)(address >> 24), (byte)(address >> 16),
            (byte)(address >> 8),  (byte)address
        };
        payload.AddRange(chunk.ToArray());
        AppendRecord(text, "S3", payload);
    }

    /// <summary>S5 — Anzahl der Datensätze (nur bis 65535 zulässig).</summary>
    private static void AppendCount(StringBuilder text, long count)
    {
        if (count > 0xFFFF) return;
        AppendRecord(text, "S5", [(byte)(count >> 8), (byte)count]);
    }

    /// <summary>S7 — Abschlusssatz mit Startadresse.</summary>
    private static void AppendTermination(StringBuilder text, long address)
    {
        AppendRecord(text, "S7",
        [
            (byte)(address >> 24), (byte)(address >> 16),
            (byte)(address >> 8),  (byte)address
        ]);
    }

    private static void AppendRecord(StringBuilder text, string type, IReadOnlyList<byte> payload)
    {
        int count = payload.Count + 1;   // Nutzdaten + Prüfsummenbyte
        int sum = count;

        text.Append(type).Append(count.ToString("X2"));
        foreach (byte b in payload)
        {
            text.Append(b.ToString("X2"));
            sum += b;
        }
        text.Append((~sum & 0xFF).ToString("X2")).Append("\r\n");
    }
}
