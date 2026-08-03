using System.Text;
using VolvoSplitter.Core;
using VolvoSplitter.Core.TriCore;

namespace VolvoSplitter.Core.Tests;

/// <summary>
/// Baut synthetische TriCore-Abbilder mit gültiger Bosch-Blockkette: Kopf nach
/// der dokumentierten Struktur, <c>blockEnd = blockStart + size - 4</c>,
/// Zeigertabellen im eigenen Block, Prüfsummenstrukturen ab +0x34 und
/// <c>0xDEADBEEF</c> am Blockende.
///
/// Die Prüfsummen stimmen wirklich: je Struktur wird ein Stellwort an das Ende
/// des geprüften Bereichs gerechnet. Für die CRC32 geht das ohne Suche, weil
/// die vier Schritte über vier Bytes umkehrbar sind — siehe
/// <see cref="CrcAdjust"/>.
///
/// <see cref="SampleMed17Image"/> baut genau die Blockkarte des ausgewerteten
/// MED17.1-Abbilds nach und ist damit die Positivkontrolle der Blockkette.
/// </summary>
public static class TriCoreDump
{
    public const uint OtpFlag = 0x00800000;

    /// <summary>Leeres (0xFF) Abbild mit Teilen an festen Datei-Offsets.</summary>
    public static byte[] Pflash(long size, params (long Start, byte[] Bytes)[] parts)
    {
        var data = new byte[size];
        Array.Fill(data, (byte)0xFF);
        foreach (var (start, bytes) in parts)
            bytes.CopyTo(data, (int)start);
        return data;
    }

    /// <summary>
    /// Baut ein VAG-Identifikationsfeld, wie es im Dataset-Block steht. Zurück
    /// kommt der ganze Bereich ab der Hardware-Teilenummer; der <c>EV_</c>-Anker
    /// liegt darin bei <c>-VagIdentBlock.HardwareOffset</c>.
    ///
    /// Alle Felder werden auf ihre Sollbreite mit Leerzeichen aufgefüllt — genau
    /// so liegen sie im Abbild.
    /// </summary>
    public static byte[] VagIdentField(string hardware, string system, string software,
                                       string index, string level,
                                       string? engine = null, params string[] engineCodes)
    {
        int anchor = -VagIdentBlock.HardwareOffset;
        int length = anchor + VagIdentBlock.EngineCodesOffset +
                     engineCodes.Length * VagIdentBlock.EngineCodeStride;

        var field = new byte[length];
        Array.Fill(field, (byte)' ');

        void Put(int offset, string value) =>
            Encoding.ASCII.GetBytes(value).CopyTo(field, anchor + offset);

        Put(VagIdentBlock.HardwareOffset, hardware);
        Put(0, system);
        Put(VagIdentBlock.SoftwareFirstOffset, software);
        Put(VagIdentBlock.IndexOffset, index);
        Put(VagIdentBlock.SoftwareOffset, software);
        Put(VagIdentBlock.LevelOffset, level);
        if (engine is not null) Put(VagIdentBlock.EngineOffset, engine);

        for (int i = 0; i < engineCodes.Length; i++)
            Put(VagIdentBlock.EngineCodesOffset + i * VagIdentBlock.EngineCodeStride,
                engineCodes[i]);

        return field;
    }

    /// <summary>
    /// Codeähnliche Füllung mit eingestreuten TriCore-Zeigern. Der Zeigeranteil
    /// ist es, was <see cref="EcuDetector.PointerDensity"/> misst.
    /// </summary>
    public static byte[] CodeBlock(int length, long cpuBase, int pointerEvery = 8)
    {
        var data = new byte[length];
        uint s = (uint)(cpuBase ^ length) | 1;

        for (int i = 0; i < length; i++)
        {
            s = s * 1664525 + 1013904223;
            data[i] = (byte)(s >> 24);
        }

        for (int at = 0; at + 4 <= length; at += pointerEvery)
        {
            s = s * 1664525 + 1013904223;
            TestDump.WriteLe(data, at, (uint)(cpuBase + (s >> 20) * 4));
        }
        return data;
    }

    /// <summary>
    /// Kennfeldähnliche Füllung: 64-Byte-Fenster mit monoton steigenden Bytes
    /// aus einem schmalen Wertebereich. Entropie um 4 bit/Byte — genau das
    /// Band, in dem Kalibrierdaten liegen.
    /// </summary>
    public static byte[] CalibrationBlock(int length)
    {
        var data = new byte[length];

        for (int i = 0; i < length; i++)
            data[i] = (byte)(0x40 + (i % 64) / 4);

        return data;
    }

    // ==================================================================
    // Bosch-Blockkette
    // ==================================================================

    /// <summary>
    /// Baut einen vollständigen Bosch-Block. <paramref name="algorithms"/> gibt
    /// je Prüfsummenstruktur das Verfahren an; der geprüfte Bereich wird
    /// gleichmäßig auf die Strukturen aufgeteilt.
    /// </summary>
    public static byte[] Block(byte id, long cpuStart, long size, long nextCpu, string identifier,
                               uint[]? table1 = null, uint[]? table2 = null,
                               byte[]? algorithms = null, uint extraFlags = 0,
                               string? variant = null)
    {
        algorithms ??= [(byte)BoschAlgorithm.Crc32];
        int count = algorithms.Length;

        var data = new byte[size];
        Fill(data, cpuStart);

        long checkWord = BoschBlockChain.HeaderSize + count * (long)BoschBlockChain.ChecksumStructureSize;
        long cursor = checkWord + 4;

        long table1Cpu = 0, table2Cpu = 0;
        if (table1 is { Length: > 0 })
        {
            table1Cpu = cpuStart + cursor;
            foreach (uint entry in table1) { TestDump.WriteLe(data, cursor, entry); cursor += 4; }
        }
        if (table2 is { Length: > 0 })
        {
            table2Cpu = cpuStart + cursor;
            foreach (uint entry in table2) { TestDump.WriteLe(data, cursor, entry); cursor += 4; }
        }

        WriteHeader(data, id, cpuStart, size, nextCpu, identifier,
                    table1Cpu, table1?.Length ?? 0, table2Cpu, table2?.Length ?? 0,
                    count, extraFlags);

        TestDump.WriteLe(data, checkWord, 0x00000000);        // Prüfwort des Blocks
        TestDump.WriteLe(data, size - 4, BoschBlockChain.EndMarker);

        long payload = (cursor + 3) & ~3L;
        if (variant is not null) WriteVariant(data, size, variant, payload);

        WriteChecksumStructures(data, id, cpuStart, size, payload, algorithms);
        return data;
    }

    private static void Fill(byte[] data, long seed)
    {
        uint s = (uint)(seed ^ data.LongLength) | 1;
        for (long i = 0; i < data.LongLength; i++)
        {
            s = s * 1664525 + 1013904223;
            data[i] = (byte)(s >> 24);
        }
    }

    private static void WriteHeader(byte[] data, byte id, long cpuStart, long size, long nextCpu,
                                    string identifier, long table1Cpu, int table1Size,
                                    long table2Cpu, int table2Size, int checksumCount, uint extraFlags)
    {
        TestDump.WriteLe(data, 0x00, id | extraFlags);
        TestDump.WriteLe(data, 0x04, (uint)size);
        TestDump.WriteLe(data, 0x08, (uint)nextCpu);
        TestDump.WriteLe(data, 0x0C, (uint)(cpuStart + size - 4));
        TestDump.WriteLe(data, 0x10, (uint)table1Cpu);
        TestDump.WriteLe(data, 0x14, (uint)table2Cpu);
        data[0x18] = (byte)table1Size;
        data[0x19] = (byte)table2Size;

        for (int i = 0; i < BoschBlockChain.SwIdentifierLength; i++)
            data[BoschBlockChain.SwIdentifierOffset + i] =
                i < identifier.Length ? (byte)identifier[i] : (byte)0xFF;

        for (int i = 0; i < BoschBlockChain.UnknownLength; i++)
            data[BoschBlockChain.UnknownOffset + i] = 0xFF;

        TestDump.WriteLe(data, BoschBlockChain.ChecksumCountOffset, (uint)checksumCount);
        TestDump.WriteLe(data, BoschBlockChain.ChecksumAdjustOffset, 0);
    }

    private static void WriteVariant(byte[] data, long size, string variant, long payload)
    {
        long at = BoschBlockChain.VariantOffset;
        if (at < payload || at + variant.Length + 1 > size - 4)
            throw new ArgumentException("Variantenkennung passt nicht in den Block.");

        Encoding.ASCII.GetBytes(variant).CopyTo(data, (int)at);
        data[at + variant.Length] = 0x00;
    }

    /// <summary>
    /// Legt die Prüfsummenstrukturen an und rechnet je Struktur ein Stellwort
    /// an das Ende ihres Bereichs, sodass die Prüfung wirklich aufgeht.
    /// </summary>
    private static void WriteChecksumStructures(byte[] data, byte id, long cpuStart, long size,
                                                long payload, byte[] algorithms)
    {
        long payloadEnd = size - 4;                 // vor 0xDEADBEEF
        int count = algorithms.Length;
        long part = (payloadEnd - payload) / count & ~3L;

        if (part < 16) throw new ArgumentException("Block zu klein für so viele Prüfsummenstrukturen.");

        for (int i = 0; i < count; i++)
        {
            long from = payload + i * part;
            long to = i == count - 1 ? payloadEnd : from + part;

            long at = BoschBlockChain.HeaderSize + i * (long)BoschBlockChain.ChecksumStructureSize;
            data[at] = id;
            TestDump.WriteLe(data, at + 0x04, (uint)(cpuStart + from));
            TestDump.WriteLe(data, at + 0x08, (uint)(cpuStart + to - 1));   // letztes Byte
            TestDump.WriteLe(data, at + 0x0C, BoschChecksum.DefaultStartValue);
            TestDump.WriteLe(data, at + 0x10, BoschChecksum.DefaultExpectedValue);
            TestDump.WriteLe(data, at + 0x14, id);
            TestDump.WriteLe(data, at + 0x18, (uint)cpuStart);
            TestDump.WriteLe(data, at + 0x1C, algorithms[i]);

            Adjust(data, from, to, algorithms[i]);
        }
    }

    /// <summary>
    /// Stellbereich, den ADD16 braucht: zwei Wörter. Das letzte zählt um 16 Bit
    /// geschoben, deckt also die obere Hälfte des Abstands ab, das vorletzte die
    /// untere — zusammen jeder 32-Bit-Abstand. CRC32 und ADD32 kommen mit einem
    /// einzigen Wort aus.
    /// </summary>
    public const int Add16AdjustBytes = 4;

    /// <summary>Stellt den Bereichsschluss so, dass der Sollwert herauskommt.</summary>
    private static void Adjust(byte[] data, long from, long to, byte algorithm)
    {
        if (algorithm == (byte)BoschAlgorithm.Add16) { AdjustAdd16(data, from, to); return; }

        var body = data.AsSpan((int)from, (int)(to - 4 - from));

        uint value = algorithm switch
        {
            (byte)BoschAlgorithm.Crc32 =>
                BoschChecksum.Crc32(body, BoschChecksum.DefaultStartValue) ^
                CrcAdjust(BoschChecksum.Crc32Residue),

            (byte)BoschAlgorithm.Add32 =>
                BoschChecksum.DefaultExpectedValue -
                BoschChecksum.Add32(body, BoschChecksum.DefaultStartValue),

            _ => 0
        };

        TestDump.WriteLe(data, to - 4, value);
    }

    /// <summary>
    /// Stellt den Bereichsschluss auf die ADD16-Sollsumme. Zwei Wörter genügen,
    /// weil das letzte um 16 Bit geschoben zählt: das vorletzte trägt die untere
    /// Hälfte des Abstands, das letzte die obere. Beide werden zuerst genullt,
    /// damit die Vorsumme feststeht.
    /// </summary>
    public static void AdjustAdd16(byte[] data, long from, long to)
    {
        if (to - from < Add16AdjustBytes)
            throw new ArgumentException(
                $"Bereich von {to - from:N0} B ist zu klein für eine ADD16-Stellgröße; " +
                $"nötig sind {Add16AdjustBytes} B.");

        long tail = to - 4;
        Array.Clear(data, (int)tail, 4);

        uint diff = BoschChecksum.DefaultExpectedValue -
                    BoschChecksum.Add16(data.AsSpan((int)from, (int)(to - from)),
                                        BoschChecksum.DefaultStartValue);

        data[tail] = (byte)diff;                // vorletztes Wort: untere Hälfte
        data[tail + 1] = (byte)(diff >> 8);
        data[tail + 2] = (byte)(diff >> 16);    // letztes Wort: obere Hälfte, zählt << 16
        data[tail + 3] = (byte)(diff >> 24);
    }

    /// <summary>
    /// Kehrt die 32 Schiebeschritte der CRC32 um. Vier angehängte Bytes wirken
    /// wie <c>R = advance32(R0 ^ x)</c>; wer <c>R</c> vorgibt, bekommt
    /// <c>x = R0 ^ advance32⁻¹(R)</c> — ohne Suche, ohne GF(2)-Löser.
    /// </summary>
    public static uint CrcAdjust(uint target)
    {
        uint s = target;
        for (int i = 0; i < 32; i++)
        {
            uint bit = s >> 31;
            s = ((s ^ (bit != 0 ? BoschChecksum.Polynomial : 0u)) << 1) | bit;
        }
        return s;
    }

    // ==================================================================
    // Container
    // ==================================================================

    /// <summary>PMU0, PMU1 und — falls vorhanden — externer Flash hintereinander.</summary>
    public static byte[] Tc1797Container(byte[] pmu0, byte[] pmu1, byte[]? external = null)
    {
        long bank = 0x200000;
        var data = new byte[bank * 2 + (external?.LongLength ?? 0)];
        Array.Fill(data, (byte)0xFF);

        pmu0.CopyTo(data, 0);
        pmu1.CopyTo(data, (int)bank);
        external?.CopyTo(data, (int)(bank * 2));

        return data;
    }

    /// <summary>
    /// Die Blockkarte des ausgewerteten MED17.1-Abbilds als fertiges 8-MiB-Bild:
    /// 2 MiB PMU0 + 2 MiB PMU1 + 4 MiB externer Flash, acht Blöcke, Kette in der
    /// beobachteten Reihenfolge 1..8 und Kettenende mit <c>nextSector == 0</c>.
    ///
    ///     Block                 CPU-Bereich          Datei          Bank
    ///     4 Customer            80000000-8000FD04    000000-00FD04  PMU0
    ///     3 Cust. tuning prot.  80010000-80012000    010000-012000  PMU0
    ///     2 Tuning protection   80014000-80017F00    014000-017F00  PMU0
    ///     1 Startup             80018000-8001FF00    018000-01FF00  PMU0
    ///     5 ASW #0              80020000-80200000    020000-200000  PMU0
    ///     6 ASW #1              80800000-80A00000    200000-400000  PMU1
    ///     8 Dataset #0          84002000-84100000    402000-500000  Extern
    ///     7 ASW #2              84100000-84380000    500000-780000  Extern
    /// </summary>
    public static byte[] SampleMed17Image()
    {
        const string UnitA = "10SW008917";
        const string UnitB = "10SW026798";
        const string UnitC = "10SW028601";

        // Die Tabelleneinträge sind absichtlich gemischt: Flash-Adresse,
        // Adresse der anderen Bank, LDRAM, Wächterwerte und eine schlichte Zahl.
        uint[] mixedTable = [0x80141F04, 0x808084AC, 0xC0000070, 0x00000000, 0xFFFFFFFF, 0x00000F7C];

        var customer = Block(0x30, 0x80000000, 0xFD04, 0x80020000, UnitA);
        var custTune = Block(0x90, 0x80010000, 0x2000, 0x80000000, UnitA, table2: [0x80011000]);
        var tuning = Block(0x20, 0x80014000, 0x3F00, 0x80010000, UnitA, extraFlags: OtpFlag);
        var startup = Block(0x10, 0x80018000, 0x7F00, 0x80014000, UnitA);
        var asw0 = Block(0x40, 0x80020000, 0x1E0000, 0x80800000, UnitB, table2: mixedTable,
                         algorithms: [0x00, 0x01, 0x10, 0x00]);
        var asw1 = Block(0x50, 0x80800000, 0x200000, 0x84100000, UnitB);
        var asw2 = Block(0xA0, 0x84100000, 0x280000, 0x84002000, UnitB);
        // Die Variantenkennung passt zum Gerät: MED17.1.6 auf TC1797, mit
        // externem Flash über die EBU — genau die 8-MiB-Form dieses Abbilds.
        var dataset = Block(0x60, 0x84002000, 0xFE000, 0, UnitC,
                            variant: "34/1/MED17.1.6/5/P643//C643X5L8///");

        var pmu0 = Pflash(0x200000,
            (0x000000, customer), (0x010000, custTune), (0x014000, tuning),
            (0x018000, startup), (0x020000, asw0));

        var pmu1 = Pflash(0x200000, (0x000000, asw1));

        var external = Pflash(0x400000, (0x002000, dataset), (0x100000, asw2));

        return Tc1797Container(pmu0, pmu1, external);
    }
}
