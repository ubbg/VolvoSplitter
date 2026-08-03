using System.Text;
using VolvoSplitter.Core.TriCore;

namespace VolvoSplitter.Core;

/// <summary>
/// Erzeugt den textuellen Befund eines Abbilds — Fahrzeug, Steuergerät, Sektoren
/// und Bereiche ohne Kopf. Gemeinsam genutzt von der Oberfläche (Zwischenablage,
/// Datei) und dem Stapelbetrieb.
/// </summary>
public static class DumpReport
{
    public static string Build(FlashDump dump)
    {
        var text = new StringBuilder();

        AppendHeadline(text, dump);
        AppendIdentity(text, dump);
        AppendSectors(text, dump);
        AppendChain(text, dump);
        AppendRegions(text, dump);
        AppendPartitions(text, dump);
        AppendEraseSectors(text, dump);

        return text.ToString();
    }

    private static void AppendHeadline(StringBuilder text, FlashDump dump)
    {
        text.AppendLine(dump.FileName);
        text.AppendLine($"{dump.Size:N0} B ({Hex.Addr(dump.Size)})  {dump.Profile.FamilyName}  " +
                        $"{dump.Profile.MicroName}  ·  {dump.Profile.Manufacturer}");

        text.AppendLine();
        text.AppendLine($"Erkennung: {dump.Detection.Summary}");
        foreach (string evidence in dump.Detection.Evidence)
            text.AppendLine($"   · {evidence}");

        if (!dump.Profile.SupportsWriteBack)
            text.AppendLine("   · Nur lesen: Prüfsummen werden gerechnet und gemeldet, nicht gestellt");
    }

    private static void AppendIdentity(StringBuilder text, FlashDump dump)
    {
        if (dump.Vehicle is { } vehicle)
        {
            text.AppendLine();
            text.AppendLine($"Fahrzeug   {vehicle.Vin}  Fahrgestell {vehicle.ChassisNumber}");
        }

        if (dump.Report is { } report)
        {
            if (report.HardwareNumber.Length > 0) text.AppendLine($"Hardware   {report.HardwareNumber}");
            if (report.SoftwareNumber.Length > 0) text.AppendLine($"Software   {report.SoftwareNumber}");
            if (report.Plugin.Length > 0) text.AppendLine($"Plugin     {report.Plugin}");
        }

        if (dump.Identity is not { Hits.Count: > 0 } identity) return;

        text.AppendLine();
        text.AppendLine("Kennungen im Abbild:");
        foreach (var hit in identity.Hits)
            text.AppendLine($"   {hit.Display}");

        if (VagEcuCatalog.Find(identity.EcuType) is { } entry)
            text.AppendLine($"   Steuergerätetabelle: {entry.Display}");
    }

    private static void AppendSectors(StringBuilder text, FlashDump dump)
    {
        if (dump.Sectors.Count == 0) return;

        text.AppendLine();
        foreach (var sector in dump.Sectors)
        {
            if (!sector.Present)
            {
                text.AppendLine($"{sector.Label,-28} {Hex.Addr(sector.Start)}   {sector.MissingReason}");
                continue;
            }

            string crc = sector.CrcOk
                ? $"Prüfsumme 0x{sector.CrcStored:X8} ok"
                : $"Prüfsumme 0x{sector.CrcStored:X8} != 0x{sector.CrcComputed:X8}";
            text.AppendLine($"{sector.Label,-28} {sector.PartNumber,-12} " +
                            $"{sector.AddressRange}   {sector.SizeText,12}   {crc}");
            if (sector.HasChecksumCopies)
                text.AppendLine($"{"",28} Prüfwert-Kopien: " +
                                string.Join(", ", sector.ChecksumCopies.Select(Hex.Addr)));
        }
    }

    /// <summary>
    /// Die Bosch-Blockkette: Reihenfolge, Art, Kennung, Zeigertabellen und die
    /// nachgerechneten Prüfsummen. Letztere sind die Kernaussage des Befunds —
    /// alles andere ist gelesen, das hier ist gerechnet.
    /// </summary>
    private static void AppendChain(StringBuilder text, FlashDump dump)
    {
        var chain = dump.Chain;
        if (chain.Blocks.Count == 0) return;

        text.AppendLine();
        text.AppendLine($"Blockkette ({chain.Blocks.Count} Blöcke, " +
                        $"{chain.ChainBlocks.Count} über nextSector erreicht):");

        foreach (var block in chain.Blocks)
        {
            string order = block.ChainOrder is { } position ? $"#{position}" : "—";
            text.AppendLine($"   {order,-3} {block.IdName,-28} {block.Identifier,-12} " +
                            $"{block.AddressRange}   {block.SizeText,12}" +
                            (block.Otp ? "   OTP" : ""));
            text.AppendLine($"       Datei {block.FileRange}   " +
                            $"nextSector {(block.NextCpu is { } n ? Hex.Addr(n) : "0 — Kettenende")}");

            foreach (var structure in block.Checksums)
                text.AppendLine($"       {structure.AlgorithmName,-16} {structure.RangeText}   " +
                                $"Start 0x{structure.StartValue:X8}   {structure.ResultText}");

            AppendTable(text, "table1", block.Table1Cpu, block.Table1);
            AppendTable(text, "table2", block.Table2Cpu, block.Table2);

            text.AppendLine($"       +0x24 (unerklärt): " +
                            string.Join(' ', block.UnknownBytes.Select(b => b.ToString("X2"))));
        }

        if (chain.Variant is { } variant)
            text.AppendLine($"   Variantenkennung: {variant}   " +
                            $"(Dataset-Block +0x{BoschBlockChain.VariantOffset:X2})");

        text.AppendLine(chain.Cvn is { } cvn
            ? $"   CVN: {cvn.ValueText} über {cvn.Ranges.Count} Bereich(e), " +
              $"Konfiguration bei {Hex.Addr(cvn.ConfigOffset)}"
            : "   CVN: nicht gefunden — die Konfigurationsstruktur ließ sich nicht bestimmen");

        if (chain.Gaps.Count > 0)
        {
            text.AppendLine($"   Von keinem Block belegt ({chain.Gaps.Count}):");
            foreach (var (from, to) in chain.Gaps)
                text.AppendLine($"       {Hex.Range(from, to)}   {to - from:N0} B");
        }
    }

    /// <summary>
    /// Zeigertabellen roh, ohne Deutung: ihre Einträge mischen Flash-, Extern-
    /// und LDRAM-Adressen mit Wächterwerten und schlichten Zahlen.
    /// </summary>
    private static void AppendTable(StringBuilder text, string name, long cpu,
                                    IReadOnlyList<uint> entries)
    {
        if (entries.Count == 0) return;

        text.AppendLine($"       {name} bei {Hex.Addr(cpu)}: " +
                        string.Join(' ', entries.Select(v => $"{v:X8}")));
    }

    private static void AppendRegions(StringBuilder text, FlashDump dump)
    {
        if (dump.Regions.Count == 0) return;

        text.AppendLine();
        text.AppendLine("Bereiche ohne Sektorkopf:");
        foreach (var region in dump.Regions)
        {
            text.AppendLine($"{region.Label,-36} {region.AddressRange}   {region.SizeText,12}" +
                            (region.CpuAddressRange is { } cpu ? $"   CPU {cpu}" : ""));
            text.AppendLine($"{"",36} {region.Description} [{region.ConfidenceText}]");
        }
    }

    private static void AppendPartitions(StringBuilder text, FlashDump dump)
    {
        if (dump.Partitions.Count == 0) return;

        text.AppendLine();
        text.AppendLine($"Physische Blöcke ({dump.Layout?.SourceNote ?? "Herkunft unbekannt"}):");

        foreach (var partition in dump.Partitions)
        {
            text.AppendLine($"{partition.Label,-22} {partition.FileRange}   " +
                            $"CPU {partition.CpuRange,-25} {partition.StateText}");
            text.AppendLine($"{"",22} {partition.Description}");
        }

        if (dump.Layout is { Complete: false })
            text.AppendLine("   Die Zuordnung ging nicht vollständig auf — siehe die Blöcke ohne " +
                            "CPU-Adresse bzw. ohne Sektorgliederung.");
    }

    private static void AppendEraseSectors(StringBuilder text, FlashDump dump)
    {
        if (dump.Layout is not { EraseSectors.Count: > 0 } layout) return;

        text.AppendLine();
        text.AppendLine($"Löschsektoren ({layout.EraseSectors.Count}):");
        foreach (var sector in layout.EraseSectors)
            text.AppendLine($"   {sector.Label,-22} {sector.FileRange}   " +
                            $"CPU {sector.CpuRange,-25} {sector.FileLength:N0} B");
    }
}
