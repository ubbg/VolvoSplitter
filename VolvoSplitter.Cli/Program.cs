using VolvoSplitter.Cli;
using VolvoSplitter.Core;
using VolvoSplitter.Core.Reporting;

// ---------------------------------------------------------------------------
// Stapelbetrieb ohne Oberfläche: zerlegt Flash-Abbilder, schreibt jeden Sektor
// bzw. Block als Rohdatei und legt einen Befund daneben. Möglich, weil die
// gesamte Analyse in VolvoSplitter.Core ohne WPF-Abhängigkeit steckt.
//
//   volvosplit <Datei|Ordner> [weitere...] [--fixed] [--profile <name>] [--out <Ordner>]
//                                          [--report-format txt|md|html]
//
// Argumentzerlegung, Dateisammlung und Zielordnerwahl stehen in CommandLine.cs;
// hier bleibt, was Konsole und Dateisystem anfasst.
// ---------------------------------------------------------------------------

// Ein Aufruffehler bricht ab, statt eine Option unter den Tisch fallen zu
// lassen — wie beim unbekannten Berichtsformat weiter unten.
if (!CommandLine.TryParse(args, out var options, out string? argError))
{
    Console.Error.WriteLine(argError);
    return 1;
}

// Ein unbekanntes Format bricht ab, statt still auf Text zurückzufallen — sonst
// bekäme man eine .txt, wo man eine .html erwartet hat.
var format = ReportFormat.Text;
if (options.ReportFormat is not null)
{
    if (!TryParseFormat(options.ReportFormat, out format))
    {
        Console.Error.WriteLine($"Unbekanntes Berichtsformat „{options.ReportFormat}\" — " +
                                "erlaubt sind txt, md und html.");
        return 1;
    }
}

if (options.ListProfiles)
{
    PrintProfiles();
    return 0;
}

if (options.Help || options.Targets.Count == 0)
{
    PrintUsage();
    return options.Help ? 0 : 1;
}

var notes = new List<string>();
var files = CommandLine.CollectFiles(options.Targets, notes);
foreach (string note in notes)
    Console.Error.WriteLine(note);

if (files.Count == 0)
{
    Console.Error.WriteLine("Keine Abbilder gefunden (.mpc / .bin / .ori).");
    return 1;
}

var planner = new TargetDirectoryPlanner();
int ok = 0;
foreach (string file in files)
{
    try
    {
        Process(file, options.FixedOnly, options.OutRoot, options.Profile, format, planner);
        ok++;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                  or InvalidOperationException or ArgumentException)
    {
        Console.Error.WriteLine($"Fehler bei {file}: {ex.Message}");
    }
}

Console.WriteLine();
Console.WriteLine($"{ok}/{files.Count} Abbilder verarbeitet.");
return ok == files.Count ? 0 : 1;

// ---------------------------------------------------------------------------

static bool TryParseFormat(string value, out ReportFormat format)
{
    format = ReportFormat.Text;

    switch (value.Trim().ToLowerInvariant())
    {
        case "txt" or "text": format = ReportFormat.Text; return true;
        case "md" or "markdown": format = ReportFormat.Markdown; return true;
        case "html" or "htm": format = ReportFormat.Html; return true;
        default: return false;
    }
}

static void Process(string path, bool fixedOnly, string? outRoot, string? profile,
                    ReportFormat format, TargetDirectoryPlanner planner)
{
    var dump = FlashDump.Load(path, fixedOnly, profile);

    string targetDir = planner.Claim(path, dump.Directory, dump.FileName, outRoot, out string? note);
    if (note is not null) Console.Error.WriteLine($"   Hinweis  {note}");
    Directory.CreateDirectory(targetDir);

    Console.WriteLine($"{dump.FileName}  ·  {dump.Profile.FamilyName}  ·  " +
                      $"{dump.Sectors.Count(s => s.Present)} Sektoren");

    foreach (string evidence in dump.Detection.Evidence)
        Console.WriteLine($"   Beleg  {evidence}");

    foreach (var sector in dump.Sectors.Where(s => s.Present))
    {
        string outPath = dump.ExtractSector(sector, targetDir);

        // Drei Zustände, drei Marken — alle vierstellig, damit die Spalten
        // dahinter stehen bleiben. „n.g." heißt „nicht gestellt": für diesen
        // Block wurde nie eine Prüfsumme gestellt, es weicht also nichts ab.
        // Der Befund daneben schreibt es aus.
        string flag = sector.Status switch
        {
            SectorStatus.Verified => "ok  ",
            SectorStatus.ChecksumNotStamped => "n.g.",
            _ => "CRC!"
        };
        Console.WriteLine($"   {flag}  {sector.Label,-28} {sector.PartNumber,-12} " +
                          $"{sector.SizeText,12}  ->  {Path.GetFileName(outPath)}");
    }

    string reportPath = Path.Combine(targetDir, $"bericht.{DumpReport.Extension(format)}");
    File.WriteAllText(reportPath, DumpReport.Build(dump, format));
    Console.WriteLine($"   Bericht  ->  {Path.GetFileName(reportPath)}");
}

static void PrintProfiles()
{
    Console.WriteLine("Profile für --profile:");
    Console.WriteLine();
    Console.WriteLine("  ems23      Volvo/TRW EMS2.3, MPC5674F   lesen und schreiben");
    Console.WriteLine("  ems24      Volvo/TRW EMS2.4, MPC5777C   lesen und schreiben");
    Console.WriteLine("  tricore    VAG/Bosch EDC17 / MED17      nur lesen, Baustein offen");
    Console.WriteLine("  tc1796     dito, mit TC1796-Sektorkarte");
    Console.WriteLine("  tc1797     dito, mit TC1797-Sektorkarte");
    Console.WriteLine("  unknown    kein Container — nur Bereiche mit Konfidenzangabe");
    Console.WriteLine();
    Console.WriteLine("Ohne --profile entscheidet die Erkennung anhand von Belegen im Abbild.");
    Console.WriteLine("Die Dateigröße allein entscheidet dabei nie zwischen Herstellern.");
}

static void PrintUsage()
{
    Console.WriteLine("""
        Flash File Splitter — Stapelbetrieb

          volvosplit <Datei|Ordner> [weitere...] [Optionen]

        Zerlegt jedes Flash-Abbild in seine Sektoren bzw. Blöcke, schreibt sie als
        Rohdateien und legt einen Befund daneben — als Text, Markdown oder HTML.

        Unterstützt werden Volvo/TRW EMS2.3 (MPC5674F) und EMS2.4 (MPC5777C) sowie
        lesend die VAG-Steuergeräte auf Infineon TriCore (Bosch EDC17 / MED17).
        Für TriCore-Abbilder werden Prüfsummen gerechnet und gemeldet, aber nicht
        gestellt — es wird nichts zurückgeschrieben.

        Optionen:
          -f, --fixed           Nur die fest verdrahteten Standardadressen lesen
          -o, --out <Ordner>    Zielordner (Standard: neben dem Abbild)
          -p, --profile <name>  Erkennung übersteuern
          -r, --report-format <f>  Befund als txt (Vorgabe), md oder html
              --list-profiles   Bekannte Profile auflisten
          -h, --help            Diese Hilfe
              --                Alles Folgende ist ein Dateiname, keine Option

        Beispiele:
          volvosplit C:\Dumps\ecu_Micro.mpc
          volvosplit C:\Dumps --out C:\Ausgabe
          volvosplit ecu.ori --profile tc1797
        """);
}
