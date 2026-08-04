namespace VolvoSplitter.Core;

/// <summary>
/// Physisches Speicherlayout des MPC5777C, wie es im .MPC-Container abgelegt ist.
///
/// Das Auslesegerät hängt drei getrennte Adressräume hintereinander in eine Datei:
///
///     Datei 0x000000–0x7FFFFF  ->  CPU 0x800000–0xFFFFFF   8 MiB Large Flash
///     Datei 0x800000–0x83FFFF  ->  CPU 0x000000–0x03FFFF   4 × 64 KiB Low/Mid
///     Datei 0x840000–0x843FFF  ->  CPU 0x400000–0x403FFF   16 KiB UTEST
///
/// Die Summe ergibt genau <see cref="ContainerSize"/> = 0x844000; es bleibt kein Byte
/// unerklärt. Die beiden 16-KiB-High-Blöcke (CSE, CPU 0x600000–0x607FFF) sind nicht
/// enthalten — mit ihnen wäre die Datei 0x84C000 groß.
///
/// Quelle: NXP MPC5777C Reference Manual, Kapitel 4, Tabelle 4-2 und 4-3
/// (im Repository als „MPC5777CRM, MPC5777C Reference Manual.pdf").
///
/// Die Zuordnung Container -> CPU ist eine Interpretation: NXP dokumentiert das
/// proprietäre Dateiformat nicht. Sie wird durch die Dateigröße, die Sektorköpfe
/// (0x740000 nennt selbst 0xF40000) und die Adressfelder im VOLVOECU-Block gestützt.
/// </summary>
public static class Mpc5777cLayout
{
    /// <summary>8 MiB Large Flash — der Teil, den die Sektortabelle beschreibt.</summary>
    public const long LargeFlashSize = 0x800000;

    /// <summary>Vier Low/Mid-Blöcke zu je 64 KiB.</summary>
    public const long LowMidBlockSize = 0x10000;
    public const long LowMidSize = 4 * LowMidBlockSize;

    /// <summary>UTEST-Block, 16 KiB.</summary>
    public const long UtestSize = 0x4000;

    /// <summary>Größe eines vollständigen MPC5777C-Containers.</summary>
    public const long ContainerSize = LargeFlashSize + LowMidSize + UtestSize;   // 0x844000

    public const string Source = "MPC5777C, NXP-Referenzhandbuch Tabelle 4-2";

    /// <summary>
    /// Layout für ein Abbild, oder null, wenn es nicht passt. Bewusst streng:
    /// nur EMS2.4 (MPC5777C) und nur ab der Größe des Large Flash.
    /// </summary>
    public static PhysicalLayout? For(EcuFamily family, long imageSize)
    {
        if (family != EcuFamily.Ems24 || imageSize < LargeFlashSize) return null;

        var partitions = new List<FlashPartition>
        {
            new("Large Flash", 0, LargeFlashSize, LargeFlashSize, FlashBlockType.Large,
                null, "Boot, Kalibrierung, Anwendungscode — 32 Sektoren zu 256 KiB")
        };

        if (imageSize == LargeFlashSize)
            return new PhysicalLayout(partitions, complete: true, Source);

        if (imageSize != ContainerSize)
        {
            // Größe passt zu keinem bekannten Container: den Anhang zeigen, aber
            // keine CPU-Adresse behaupten.
            partitions.Add(new FlashPartition("Anhang", LargeFlashSize, imageSize - LargeFlashSize,
                null, FlashBlockType.Unknown, null,
                "Größe passt zu keinem bekannten Containerlayout"));
            return new PhysicalLayout(partitions, complete: false, Source);
        }

        // Vier 64-KiB-Blöcke: zwei Low, zwei Mid. Blocknummer zählt je Typ neu,
        // die RWW-Partition durchgehend 0..3 (RM Tabelle 4-2).
        for (int i = 0; i < 4; i++)
        {
            bool low = i < 2;
            partitions.Add(new FlashPartition(
                $"{(low ? "Low" : "Mid")}-Block {i % 2}",
                LargeFlashSize + i * LowMidBlockSize, LowMidBlockSize, i * LowMidBlockSize,
                low ? FlashBlockType.Low : FlashBlockType.Mid, i,
                "NXP-Beispielnutzung: EEPROM-Daten")
            {
                EmulatedEeprom = true
            });
        }

        partitions.Add(new FlashPartition("UTEST", LargeFlashSize + LowMidSize, UtestSize,
            0x400000, FlashBlockType.Utest, 0,
            "Test-, Security-, DCF- und Kunden-OTP-Daten — einmal programmierbar")
        {
            Otp = true
        });

        return new PhysicalLayout(partitions, complete: true, Source);
    }
}
