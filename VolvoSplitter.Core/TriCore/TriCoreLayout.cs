namespace VolvoSplitter.Core.TriCore;

/// <summary>
/// Bildet ein TriCore-Abbild auf die physischen Bänke des Bausteins ab.
///
/// Auslesewerkzeuge legen die Bänke hintereinander in eine Datei, beginnend
/// bei PMU0. Was hinter dem internen Flash steht, hängt vom Gerät ab:
///
///     4 MiB intern + 4 MiB extern   MED17.1 mit externem Flash über die EBU
///     intern + 0x10000 / 0x20000    DFLASH angehängt
///
/// Die 8-MiB-Form ist unabhängig belegt: die Bosch-Blockkette eines solchen
/// Abbilds nennt in ihren Blockköpfen selbst 0x80800000 für PMU1 und
/// 0x84000000 für den externen Flash (siehe <see cref="BoschBlockChain"/>).
///
/// Alles andere bleibt „Anhang" ohne CPU-Adresse — wie es
/// <see cref="Mpc5777cLayout"/> für unbekannte Containergrößen schon tut.
///
/// <strong>Der Nullpunkt ist ein Parameter, keine Setzung.</strong> Ein Abbild
/// ist ein <em>Fenster</em> in den Adressraum des Bausteins, und es beginnt
/// nicht zwangsläufig an dessen Anfang: eine Teilauslesung ab
/// <c>0x80180000</c> ist im Bestand der Normalfall, keine Ausnahme. Wo dieses
/// Fenster anfängt, sagen die Blockköpfe des Abbilds selbst
/// (<see cref="BoschBlockChain.MeasureWindowStarts"/>) — hier kommt es als
/// <c>windowStart</c> herein. Ohne Angabe gilt weiterhin der Anfang des
/// Bausteins, damit sich für jeden bestehenden Aufruf nichts ändert.
/// </summary>
public static class TriCoreLayout
{
    /// <summary>Kleinster Rest, der überhaupt als externer Flash in Frage kommt.</summary>
    private const long MinExternal = 0x40000;

    /// <param name="windowStart">
    /// CPU-Adresse, die zum Datei-Offset 0 gehört. Null heißt: der Anfang des
    /// Bausteins — das bisherige Verhalten.
    /// </param>
    public static PhysicalLayout For(TriCoreDevice device, long imageSize, long? windowStart = null)
    {
        if (imageSize <= 0)
            return new PhysicalLayout([], complete: false, device.SourceNote);

        long start = windowStart ?? DefaultWindowStart(device);

        // Baustein ohne hinterlegte Sektorkarte: eine einzige PFLASH-Partition.
        // Die Adressen stimmen, die Gliederung fehlt — und das wird gesagt.
        // Ein gemessener Nullpunkt landet immer hier: er sagt, wo das Fenster
        // anfängt, und nichts über Bänke.
        if (device.Program.Count == 0)
            return SingleWindow(device, imageSize, start, "Bank- und Sektoreinteilung nicht hinterlegt");

        // Der Fensteranfang muss in einer Bank dieses Bausteins liegen. Täte er
        // es nicht, würde der Bankdurchlauf ihn stillschweigend auf die nächste
        // Bank aufrunden und eine Adresse behaupten, die niemand gemessen hat.
        if (!device.Program.Any(b => start >= b.CpuStart && start < b.CpuEnd))
            return SingleWindow(device, imageSize, start,
                $"Fensteranfang {Hex.Addr(start)} liegt in keiner Bank von {device.Name} — " +
                "Bankaufteilung nicht anwendbar");

        var partitions = new List<FlashPartition>();
        var eraseSectors = new List<FlashPartition>();
        bool complete = true;
        long cursor = 0;

        foreach (var bank in device.Program)
        {
            if (cursor >= imageSize) break;

            // Bänke, die ganz vor dem Fenster liegen, stehen nicht in der Datei.
            if (bank.CpuEnd <= start) continue;

            // Einstieg in die erste angeschnittene Bank; bei allen folgenden 0.
            long into = Math.Max(0, start - bank.CpuStart);
            long take = Math.Min(bank.Size - into, imageSize - cursor);
            bool partial = take < bank.Size;

            partitions.Add(new FlashPartition(bank.Label, cursor, take, bank.CpuStart + into,
                FlashBlockType.Program, null,
                partial
                    ? "Bank nur teilweise im Abbild — für diese Ausführung ist keine " +
                      "Sektoraufteilung dokumentiert"
                    : $"Programmflash — {bank.SectorSizes.Count} Löschsektoren")
            {
                EmulatedEeprom = bank.EmulatedEeprom
            });

            if (partial)
                complete = false;
            else
                AddEraseSectors(eraseSectors, bank, cursor);

            cursor += take;
        }

        if (cursor < imageSize)
            complete &= AppendTail(partitions, device, cursor, imageSize);

        return new PhysicalLayout(partitions, complete, device.SourceNote, eraseSectors);
    }

    /// <summary>Anfang des Bausteins — der Nullpunkt, wenn keiner gemessen wurde.</summary>
    private static long DefaultWindowStart(TriCoreDevice device) =>
        device.Program.Count > 0 ? device.Program[0].CpuStart : TriCoreDevice.PflashBase;

    /// <summary>
    /// Das ganze Abbild als eine Partition ab <paramref name="start"/>. Die
    /// Adressen stimmen, die Gliederung fehlt — deshalb <c>Complete: false</c>.
    /// </summary>
    private static PhysicalLayout SingleWindow(TriCoreDevice device, long imageSize, long start,
                                               string note) =>
        new([new FlashPartition("PFLASH", 0, imageSize, start, FlashBlockType.Program, null, note)],
            complete: false, device.SourceNote);

    /// <summary>
    /// Ordnet den Teil hinter dem internen Flash zu. Gibt false zurück, wenn die
    /// Zuordnung nicht durch Datenblatt oder Beispielabbild gedeckt ist.
    /// </summary>
    private static bool AppendTail(List<FlashPartition> partitions, TriCoreDevice device,
                                   long from, long imageSize)
    {
        long rest = imageSize - from;

        // Angehängter DFLASH: nur bei exakter Übereinstimmung mit der Größe aus
        // dem Datenblatt. Ein Rest beliebiger Größe ist kein EEPROM.
        if (device.Data is { } dflash && rest == dflash.Size)
        {
            partitions.Add(new FlashPartition(dflash.Label, from, rest, dflash.CpuStart,
                FlashBlockType.DataFlash, null,
                $"Datenflash, {dflash.SectorSizes.Count} Bänke — trägt die EEPROM-Emulation")
            {
                EmulatedEeprom = true
            });
            return true;
        }

        if (device.ExternalBusBase is { } external && rest >= MinExternal)
        {
            // Die Zuordnung ist für die 8-MiB-Form (4 MiB intern + 4 MiB extern)
            // durch ein ausgewertetes MED17.1-Abbild belegt. Bei anderen Größen
            // ist sie nur plausibel — die Adressen werden trotzdem gesetzt,
            // damit Zeiger auflösbar bleiben, aber das Layout gilt als unvollständig.
            bool documented = device.ProgramSize == 0x400000 && rest == 0x400000;

            partitions.Add(new FlashPartition("Externer Flash (EBU)", from, rest, external,
                FlashBlockType.Program, null,
                documented
                    ? "Externer Flash am EBU — Lage durch ein ausgewertetes MED17.1-Abbild belegt"
                    : "Externer Flash am EBU — Lage plausibel, für diese Größe nicht belegt"));
            return documented;
        }

        partitions.Add(new FlashPartition("Anhang", from, rest, null,
            FlashBlockType.Unknown, null,
            "Größe passt zu keinem bekannten Containerlayout"));
        return false;
    }

    private static void AddEraseSectors(List<FlashPartition> into, FlashBank bank, long bankFileStart)
    {
        long at = 0;
        for (int i = 0; i < bank.SectorSizes.Count; i++)
        {
            long size = bank.SectorSizes[i];
            into.Add(new FlashPartition(
                $"{bank.Label} S{i} ({size / 1024} KiB)",
                bankFileStart + at, size, bank.CpuStart + at,
                FlashBlockType.Program, null, "Löschsektor")
            {
                EmulatedEeprom = bank.EmulatedEeprom
            });
            at += size;
        }
    }
}
