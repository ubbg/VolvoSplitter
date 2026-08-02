using System.Text;

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
        text.AppendLine(dump.FileName);
        text.AppendLine($"{dump.Size:N0} B (0x{dump.Size:X6})  {FlashFormat.FamilyName(dump.Family)}  " +
                        FlashFormat.MicroName(dump.Family));

        if (dump.Vehicle is { } vehicle)
            text.AppendLine($"Fahrzeug   {vehicle.Vin}  Fahrgestell {vehicle.ChassisNumber}");
        if (dump.Report is { } report)
        {
            if (report.HardwareNumber.Length > 0) text.AppendLine($"Hardware   {report.HardwareNumber}");
            if (report.SoftwareNumber.Length > 0) text.AppendLine($"Software   {report.SoftwareNumber}");
            if (report.Plugin.Length > 0) text.AppendLine($"Plugin     {report.Plugin}");
        }

        text.AppendLine();
        foreach (var sector in dump.Sectors)
        {
            if (!sector.Present)
            {
                text.AppendLine($"{sector.Label,-14} 0x{sector.Start:X6}   {sector.MissingReason}");
                continue;
            }

            string crc = sector.CrcOk
                ? $"CRC 0x{sector.CrcStored:X8} ok"
                : $"CRC 0x{sector.CrcStored:X8} != 0x{sector.CrcComputed:X8}";
            text.AppendLine($"{sector.Label,-14} {sector.PartNumber}   " +
                            $"{sector.AddressRange}   {sector.SizeText,12}   {crc}");
            if (sector.HasChecksumCopies)
                text.AppendLine($"{"",14} Prüfwert-Kopien: " +
                                string.Join(", ", sector.ChecksumCopies.Select(a => $"0x{a:X6}")));
        }

        if (dump.Regions.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Bereiche ohne Sektorkopf:");
            foreach (var region in dump.Regions)
                text.AppendLine($"{region.Label,-14} {region.AddressRange}   " +
                                $"{region.SizeText,12}   {region.Description}");
        }

        return text.ToString();
    }
}
