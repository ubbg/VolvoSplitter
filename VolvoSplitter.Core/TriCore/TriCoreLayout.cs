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
/// </summary>
public static class TriCoreLayout
{
    /// <summary>Kleinster Rest, der überhaupt als externer Flash in Frage kommt.</summary>
    private const long MinExternal = 0x40000;

    public static PhysicalLayout For(TriCoreDevice device, long imageSize)
    {
        if (imageSize <= 0)
            return new PhysicalLayout([], complete: false, device.SourceNote);

        // Baustein ohne hinterlegte Sektorkarte: eine einzige PFLASH-Partition.
        // Die Adressen stimmen, die Gliederung fehlt — und das wird gesagt.
        if (device.Program.Count == 0)
            return new PhysicalLayout(
                [new FlashPartition("PFLASH", 0, imageSize, TriCoreDevice.PflashBase,
                                    FlashBlockType.Program, null,
                                    "Bank- und Sektoreinteilung nicht hinterlegt")],
                complete: false, device.SourceNote);

        var partitions = new List<FlashPartition>();
        var eraseSectors = new List<FlashPartition>();
        bool complete = true;
        long cursor = 0;

        foreach (var bank in device.Program)
        {
            if (cursor >= imageSize) break;

            long take = Math.Min(bank.Size, imageSize - cursor);
            bool partial = take < bank.Size;

            partitions.Add(new FlashPartition(bank.Label, cursor, take, bank.CpuStart,
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
