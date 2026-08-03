namespace VolvoSplitter.Core.TriCore;

/// <summary>
/// Die drei Prüfalgorithmen, die eine Bosch-Prüfsummenstruktur benennen kann.
/// Die Bezeichner stammen aus dem ausgewerteten Werkzeug, nicht aus einer
/// Bosch-Unterlage — sie werden übernommen, aber als Herkunft gekennzeichnet.
/// </summary>
public enum BoschAlgorithm
{
    /// <summary>0x00 — SB_CRC32_ALGO_E.</summary>
    Crc32 = 0x00,

    /// <summary>0x01 — SB_ADD32_ALGO_E.</summary>
    Add32 = 0x01,

    /// <summary>0x10 — SB_ADD16_ALGO_E.</summary>
    Add16 = 0x10
}

/// <summary>
/// Rechnet die drei Prüfverfahren nach, die in den Prüfsummenstrukturen eines
/// Bosch-Blocks stehen. Alle drei laufen mit dem Startwert aus der Struktur —
/// im Regelfall <c>0xFADECAFE</c>.
///
///     ID    Name              Verfahren                                Soll
///     0x00  SB_CRC32_ALGO_E   CRC32, Polynom 0xEDB88320, bitweise      0x35015001
///     0x01  SB_ADD32_ALGO_E   Summe der u32-Doppelworte (LE)           csExpectedVal
///     0x10  SB_ADD16_ALGO_E   Summe der u16-Worte (LE)                 csExpectedVal
///
/// <c>0x35015001</c> ist das Einerkomplement von <c>0xCAFEAFFE</c>: die CRC32
/// läuft ohne Schlussabgleich, und der geprüfte Bereich schließt das Stellwort
/// mit ein, sodass das Register am Ende auf dem Restwert steht.
///
/// Erst eine nachgerechnete Prüfsumme rechtfertigt <c>SectorStatus.Verified</c>.
/// Gerechnet, nicht gestellt: eine Korrektur ist ausdrücklich nicht Aufgabe
/// dieses Werkzeugs.
/// </summary>
public static class BoschChecksum
{
    public const uint DefaultStartValue = 0xFADECAFE;
    public const uint DefaultExpectedValue = 0xCAFEAFFE;

    /// <summary>Restwert der CRC32 — das Einerkomplement von <see cref="DefaultExpectedValue"/>.</summary>
    public const uint Crc32Residue = 0x35015001;

    public const uint Polynomial = 0xEDB88320;

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? Polynomial ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    /// <summary>
    /// Die bitweise Definition. Langsam, aber die maßgebliche Fassung — die
    /// Tabellenvariante darf nur benutzt werden, solange sie zeichengenau
    /// dasselbe rechnet (siehe Test <c>Crc32TableMatchesBitwise</c>).
    /// </summary>
    public static uint Crc32Bitwise(ReadOnlySpan<byte> data, uint start)
    {
        uint crc = start;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? Polynomial ^ (crc >> 1) : crc >> 1;
        }
        return crc;
    }

    /// <summary>Dieselbe Rechnung tabellengetrieben.</summary>
    public static uint Crc32(ReadOnlySpan<byte> data, uint start)
    {
        uint crc = start;
        foreach (byte b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    /// <summary>Summe der 32-Bit-Doppelworte, Überlauf verworfen. Rest am Ende wird mitgezählt.</summary>
    public static uint Add32(ReadOnlySpan<byte> data, uint start)
    {
        uint sum = start;
        int at = 0;
        for (; at + 4 <= data.Length; at += 4)
            sum += ByteOrder.ReadUInt32(data, at, Endianness.Little);
        return sum;
    }

    /// <summary>Summe der 16-Bit-Worte, Überlauf verworfen.</summary>
    public static uint Add16(ReadOnlySpan<byte> data, uint start)
    {
        uint sum = start;
        int at = 0;
        for (; at + 2 <= data.Length; at += 2)
            sum += ByteOrder.ReadUInt16(data, at, Endianness.Little);
        return sum;
    }

    /// <summary>Rechnet nach, was die Struktur verlangt. Null bei unbekannter Kennung.</summary>
    public static uint? Compute(byte algorithm, ReadOnlySpan<byte> data, uint start) => algorithm switch
    {
        (byte)BoschAlgorithm.Crc32 => Crc32(data, start),
        (byte)BoschAlgorithm.Add32 => Add32(data, start),
        (byte)BoschAlgorithm.Add16 => Add16(data, start),
        _ => null
    };

    /// <summary>Sollergebnis. Null bei unbekannter Kennung — es wird keines geraten.</summary>
    public static uint? Expected(byte algorithm, uint expectedFromStructure) => algorithm switch
    {
        (byte)BoschAlgorithm.Crc32 => Crc32Residue,
        (byte)BoschAlgorithm.Add32 or (byte)BoschAlgorithm.Add16 => expectedFromStructure,
        _ => null
    };

    public static string Name(byte algorithm) => algorithm switch
    {
        (byte)BoschAlgorithm.Crc32 => "SB_CRC32_ALGO_E",
        (byte)BoschAlgorithm.Add32 => "SB_ADD32_ALGO_E",
        (byte)BoschAlgorithm.Add16 => "SB_ADD16_ALGO_E",
        _ => $"unbekannt (0x{algorithm:X2})"
    };

    public static bool IsKnown(byte algorithm) =>
        algorithm is (byte)BoschAlgorithm.Crc32
                  or (byte)BoschAlgorithm.Add32
                  or (byte)BoschAlgorithm.Add16;

    // ------------------------------------------------------------------
    // CVN — Calibration Verification Number
    // ------------------------------------------------------------------

    /// <summary>
    /// Die CVN ist eine gewöhnliche CRC32 (Start 0xFFFFFFFF, Schlussabgleich
    /// 0xFFFFFFFF) über mehrere Speicherbereiche nacheinander. Sie wird gelesen
    /// und ausgewiesen — das ist die Zahl, die die OBD-Diagnose zur Prüfung der
    /// Kalibrierung meldet.
    /// </summary>
    public const uint CvnStart = 0xFFFFFFFF;

    public static uint CvnUpdate(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    public static uint CvnFinish(uint crc) => crc ^ 0xFFFFFFFFu;
}
