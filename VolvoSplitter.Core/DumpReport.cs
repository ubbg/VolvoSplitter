using VolvoSplitter.Core.Reporting;
using VolvoSplitter.Core.TriCore;

namespace VolvoSplitter.Core;

/// <summary>
/// Erzeugt den Befund eines Abbilds — Fahrzeug, Steuergerät, Sektoren, Blöcke
/// und Bereiche ohne Kopf. Gemeinsam genutzt von der Oberfläche (Zwischenablage,
/// Datei) und dem Stapelbetrieb.
///
/// Der Bericht wird <em>einmal aufgebaut</em> (<see cref="Compose"/>) und
/// wahlweise als Text, Markdown oder HTML ausgegeben. Ein neuer Abschnitt
/// erscheint dadurch in allen drei Formaten, ohne dass ein Ausgeber angefasst
/// werden muss.
/// </summary>
public static class DumpReport
{
    /// <summary>Bericht als reiner Text — die Vorgabe seit jeher.</summary>
    public static string Build(FlashDump dump) => Build(dump, ReportFormat.Text);

    public static string Build(FlashDump dump, ReportFormat format)
    {
        var document = Compose(dump);

        return format switch
        {
            ReportFormat.Markdown => MarkdownReportWriter.Write(document),
            ReportFormat.Html => HtmlReportWriter.Write(document),
            _ => TextReportWriter.Write(document)
        };
    }

    public static string Extension(ReportFormat format) => format switch
    {
        ReportFormat.Markdown => "md",
        ReportFormat.Html => "html",
        _ => "txt"
    };

    /// <summary>
    /// Format zu einer Endung oder einem Dateinamen. Unbekanntes ergibt Text —
    /// die Endung entscheidet, nicht die Reihenfolge im Dateidialog, damit auch
    /// eine von Hand getippte Endung zählt.
    /// </summary>
    public static ReportFormat FormatFor(string pathOrExtension)
    {
        string extension = (Path.GetExtension(pathOrExtension) is { Length: > 0 } found
                               ? found
                               : pathOrExtension)
                           .TrimStart('.')
                           .ToLowerInvariant();

        return extension switch
        {
            "md" or "markdown" => ReportFormat.Markdown,
            "html" or "htm" => ReportFormat.Html,
            _ => ReportFormat.Text
        };
    }

    // ==================================================================
    // Aufbau
    // ==================================================================

    public static ReportDocument Compose(FlashDump dump)
    {
        var report = new ReportBuilder();

        ComposeHeadline(report, dump);
        ComposeIdentity(report, dump);
        ComposeSectors(report, dump);
        ComposeChain(report, dump);
        ComposeRegions(report, dump);
        ComposePartitions(report, dump);
        ComposeEraseSectors(report, dump);

        return report.Build(dump.FileName);
    }

    private static void ComposeHeadline(ReportBuilder report, FlashDump dump)
    {
        report.Paragraph($"{dump.Size:N0} B ({Hex.Addr(dump.Size)})  {dump.Profile.FamilyName}  " +
                         $"{dump.Profile.MicroName}  ·  {dump.Profile.Manufacturer}");

        report.Heading($"Erkennung: {dump.Detection.Summary}");

        var evidence = dump.Detection.Evidence.ToList();
        if (!dump.Profile.SupportsWriteBack)
            evidence.Add("Nur lesen: Prüfsummen werden gerechnet und gemeldet, nicht gestellt");

        report.Bullets(evidence);
    }

    private static void ComposeIdentity(ReportBuilder report, FlashDump dump)
    {
        var fields = new List<(string, string)>();

        if (dump.Vehicle is { } vehicle)
        {
            fields.Add(("Fahrzeug", vehicle.Vin));
            fields.Add(("Fahrgestell", vehicle.ChassisNumber));
        }

        if (dump.Report is { } protocol)
        {
            if (protocol.HardwareNumber.Length > 0) fields.Add(("Hardware", protocol.HardwareNumber));
            if (protocol.SoftwareNumber.Length > 0) fields.Add(("Software", protocol.SoftwareNumber));
            if (protocol.Plugin.Length > 0) fields.Add(("Plugin", protocol.Plugin));
        }

        if (fields.Count > 0)
        {
            report.Heading("Fahrzeug und Steuergerät");
            report.Fields(fields);
        }

        if (dump.Identity is not { Hits.Count: > 0 } identity) return;

        report.Heading("Kennungen im Abbild");
        report.Table(["Art", "Wert", "Fundort", "Beleglage"],
                     identity.Hits.Select(hit => new ReportRow(
                         [hit.Kind, hit.Value, Hex.Addr(hit.Offset), hit.ConfidenceText])));

        if (VagEcuCatalog.Find(identity.EcuType) is { } entry)
            report.Paragraph($"Steuergerätetabelle: {entry.Display}");
    }

    private static void ComposeSectors(ReportBuilder report, FlashDump dump)
    {
        if (dump.Sectors.Count == 0) return;

        report.Heading("Sektoren");

        var rows = new List<ReportRow>();
        foreach (var sector in dump.Sectors)
        {
            if (!sector.Present)
            {
                rows.Add(new ReportRow(
                    [sector.Label, "", Hex.Addr(sector.Start), "", sector.MissingReason],
                    RowMood.Warning));
                continue;
            }

            // Drei Zustände, drei Texte. „nicht gestellt" ist keine Abweichung:
            // der Vergleichswert fehlt, statt zu widersprechen — deshalb steht
            // dort auch kein „!=" und keine Warnfarbe.
            string crc = sector.Status switch
            {
                SectorStatus.Verified => $"0x{sector.CrcStored:X8} ok",
                SectorStatus.ChecksumNotStamped =>
                    $"nicht gestellt — gerechnet 0x{sector.CrcComputed:X8}",
                _ => $"0x{sector.CrcStored:X8} != 0x{sector.CrcComputed:X8}"
            };

            rows.Add(new ReportRow(
                [sector.Label, sector.PartNumber, sector.AddressRange, sector.SizeText, crc],
                sector.Status switch
                {
                    SectorStatus.Verified => RowMood.Good,
                    SectorStatus.ChecksumNotStamped => RowMood.Normal,
                    _ => RowMood.Warning
                }));

            if (sector.HasChecksumCopies)
                rows.Add(new ReportRow(
                    ["", "", "", "", "Prüfwert-Kopien: " +
                                     string.Join(", ", sector.ChecksumCopies.Select(Hex.Addr))]));
        }

        report.Table(["Sektor", "Teilenummer", "Bereich", "Größe", "Prüfsumme"], rows);
    }

    /// <summary>
    /// Die Bosch-Blockkette: Reihenfolge, Art, Kennung, Zeigertabellen und die
    /// nachgerechneten Prüfsummen. Letztere sind die Kernaussage des Befunds —
    /// alles andere ist gelesen, das hier ist gerechnet.
    /// </summary>
    private static void ComposeChain(ReportBuilder report, FlashDump dump)
    {
        var chain = dump.Chain;
        if (chain.Blocks.Count == 0) return;

        report.Heading($"Blockkette — {chain.Blocks.Count} Blöcke, " +
                       $"{chain.ChainBlocks.Count} über nextSector erreicht");

        foreach (var block in chain.Blocks)
        {
            string order = block.ChainOrder is { } position ? $"#{position}" : "—";
            string title = $"{order}  {block.IdName}" +
                           (block.Identifier.Length > 0 ? $"  ·  {block.Identifier}" : "") +
                           (block.Otp ? "  ·  OTP" : "");

            report.Group(title, block1 =>
            {
                block1.Fields(
                [
                    ("CPU-Bereich", block.AddressRange),
                    ("Datei", block.FileRange),
                    ("Größe", block.SizeText),
                    ("nextSector", block.NextCpu is { } next ? Hex.Addr(next) : "0 — Kettenende"),

                    // Steht hier, weil die Prüfsummenzeilen darunter sich darauf
                    // berufen: ohne gestelltes Stellwort gibt es nichts, wogegen
                    // eine Prüfsumme stimmen könnte.
                    ("checksumAdjust", $"0x{block.ChecksumAdjust:X8}" +
                                       (block.ChecksumNotStamped ? " — nie gestellt" : ""))
                ]);

                block1.Table(["Verfahren", "Bereich", "Startwert", "Ergebnis"],
                             block.Checksums.Select(structure => new ReportRow(
                                 [structure.AlgorithmName, structure.RangeText,
                                  $"0x{structure.StartValue:X8}", structure.ResultText],
                                 structure.Ok switch
                                 {
                                     true => RowMood.Good,
                                     false when !structure.NotStamped => RowMood.Warning,
                                     _ => RowMood.Normal
                                 })));

                var raw = new List<string>();
                AppendTable(raw, "table1", block.Table1Cpu, block.Table1);
                AppendTable(raw, "table2", block.Table2Cpu, block.Table2);
                raw.Add("+0x24 (unerklärt): " +
                        string.Join(' ', block.UnknownBytes.Select(b => b.ToString("X2"))));

                block1.Preformatted(string.Join('\n', raw));
            });
        }

        var notes = new List<string>();

        if (chain.Variant is { } variant)
            notes.Add($"Variantenkennung: {variant}   " +
                      $"(Dataset-Block +0x{BoschBlockChain.VariantOffset:X2})");

        notes.Add(chain.Cvn is { } cvn
            ? $"CVN: {cvn.ValueText} über {cvn.Ranges.Count} Bereich(e), " +
              $"Konfiguration bei {Hex.Addr(cvn.ConfigOffset)}"
            : "CVN: nicht gefunden — die Konfigurationsstruktur ließ sich nicht bestimmen");

        report.Bullets(notes);

        if (chain.Gaps.Count == 0) return;

        report.Heading($"Von keinem Block belegt — {chain.Gaps.Count} Bereich(e)");
        report.Table(["Bereich", "Größe"],
                     chain.Gaps.Select(gap => new ReportRow(
                         [Hex.Range(gap.From, gap.To), $"{gap.To - gap.From:N0} B"])));
    }

    /// <summary>
    /// Zeigertabellen roh, ohne Deutung: ihre Einträge mischen Flash-, Extern-
    /// und LDRAM-Adressen mit Wächterwerten und schlichten Zahlen.
    /// </summary>
    private static void AppendTable(List<string> into, string name, long cpu,
                                    IReadOnlyList<uint> entries)
    {
        if (entries.Count == 0) return;

        into.Add($"{name} bei {Hex.Addr(cpu)}: " +
                 string.Join(' ', entries.Select(v => $"{v:X8}")));
    }

    private static void ComposeRegions(ReportBuilder report, FlashDump dump)
    {
        if (dump.Regions.Count == 0) return;

        report.Heading("Bereiche ohne Sektorkopf");
        report.Table(["Bereich", "Adressen", "Größe", "CPU", "Befund", "Beleglage"],
                     dump.Regions.Select(region => new ReportRow(
                         [region.Label, region.AddressRange, region.SizeText,
                          region.CpuAddressRange ?? "—", region.Description,
                          region.ConfidenceText])));
    }

    private static void ComposePartitions(ReportBuilder report, FlashDump dump)
    {
        if (dump.Partitions.Count == 0) return;

        report.Heading($"Physische Blöcke — {dump.Layout?.SourceNote ?? "Herkunft unbekannt"}");
        report.Table(["Block", "Datei", "CPU", "Zustand", "Bedeutung"],
                     dump.Partitions.Select(partition => new ReportRow(
                         [partition.Label, partition.FileRange, partition.CpuRange,
                          partition.StateText, partition.Description])));

        if (dump.Layout is { Complete: false })
            report.Paragraph("Die Zuordnung ging nicht vollständig auf — siehe die Blöcke ohne " +
                             "CPU-Adresse bzw. ohne Sektorgliederung.");
    }

    private static void ComposeEraseSectors(ReportBuilder report, FlashDump dump)
    {
        if (dump.Layout is not { EraseSectors.Count: > 0 } layout) return;

        report.Heading($"Löschsektoren — {layout.EraseSectors.Count}");
        report.Table(["Sektor", "Datei", "CPU", "Größe"],
                     layout.EraseSectors.Select(sector => new ReportRow(
                         [sector.Label, sector.FileRange, sector.CpuRange ?? "—",
                          $"{sector.FileLength:N0} B"])));
    }
}
