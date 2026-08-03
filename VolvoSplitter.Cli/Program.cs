using VolvoSplitter.Core;
using VolvoSplitter.Core.Reporting;

// ---------------------------------------------------------------------------
// Stapelbetrieb ohne Oberfläche: zerlegt Flash-Abbilder, schreibt jeden Sektor
// bzw. Block als Rohdatei und legt einen Befund daneben. Möglich, weil die
// gesamte Analyse in VolvoSplitter.Core ohne WPF-Abhängigkeit steckt.
//
//   volvosplit <Datei|Ordner> [weitere...] [--fixed] [--profile <name>] [--out <Ordner>]
//                                          [--report-format txt|md|html]
// ---------------------------------------------------------------------------

ParseArgs(args, out var targets, out bool fixedOnly, out string? outRoot, out string? profile,
          out string? reportFormat, out bool listProfiles, out bool help);

// Ein unbekanntes Format bricht ab, statt still auf Text zurückzufallen — sonst
// bekäme man eine .txt, wo man eine .html erwartet hat.
var format = ReportFormat.Text;
if (reportFormat is not null)
{
    if (!TryParseFormat(reportFormat, out format))
    {
        Console.Error.WriteLine($"Unbekanntes Berichtsformat „{reportFormat}\" — " +
                                "erlaubt sind txt, md und html.");
        return 1;
    }
}

if (listProfiles)
{
    PrintProfiles();
    return 0;
}

if (help || targets.Count == 0)
{
    PrintUsage();
    return help ? 0 : 1;
}

var files = CollectFiles(targets);
if (files.Count == 0)
{
    Console.Error.WriteLine("Keine Abbilder gefunden (.mpc / .bin / .ori).");
    return 1;
}

int ok = 0;
foreach (string file in files)
{
    try
    {
        Process(file, fixedOnly, outRoot, profile, format);
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
                    ReportFormat format)
{
    var dump = FlashDump.Load(path, fixedOnly, profile);

    string targetDir = outRoot is null
        ? Path.Combine(dump.Directory, Path.GetFileNameWithoutExtension(dump.FileName) + "_sektoren")
        : Path.Combine(outRoot, Path.GetFileNameWithoutExtension(dump.FileName));
    Directory.CreateDirectory(targetDir);

    Console.WriteLine($"{dump.FileName}  ·  {dump.Profile.FamilyName}  ·  " +
                      $"{dump.Sectors.Count(s => s.Present)} Sektoren");

    foreach (string evidence in dump.Detection.Evidence)
        Console.WriteLine($"   Beleg  {evidence}");

    foreach (var sector in dump.Sectors.Where(s => s.Present))
    {
        string outPath = dump.ExtractSector(sector, targetDir);
        string flag = sector.CrcOk ? "ok  " : "CRC!";
        Console.WriteLine($"   {flag}  {sector.Label,-28} {sector.PartNumber,-12} " +
                          $"{sector.SizeText,12}  ->  {Path.GetFileName(outPath)}");
    }

    string reportPath = Path.Combine(targetDir, $"bericht.{DumpReport.Extension(format)}");
    File.WriteAllText(reportPath, DumpReport.Build(dump, format));
    Console.WriteLine($"   Bericht  ->  {Path.GetFileName(reportPath)}");
}

static List<string> CollectFiles(List<string> targets)
{
    string[] patterns = ["*.mpc", "*.bin", "*.ori"];
    var files = new List<string>();

    foreach (string target in targets)
    {
        if (Directory.Exists(target))
        {
            foreach (string pattern in patterns)
                files.AddRange(Directory.EnumerateFiles(target, pattern, SearchOption.TopDirectoryOnly));
        }
        else if (File.Exists(target))
        {
            files.Add(target);
        }
        else
        {
            Console.Error.WriteLine($"Nicht gefunden: {target}");
        }
    }
    return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

static void ParseArgs(string[] args, out List<string> targets, out bool fixedOnly,
                      out string? outRoot, out string? profile, out string? reportFormat,
                      out bool listProfiles, out bool help)
{
    targets = [];
    fixedOnly = false;
    outRoot = null;
    profile = null;
    reportFormat = null;
    listProfiles = false;
    help = false;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--fixed" or "-f":
                fixedOnly = true;
                break;
            case "--out" or "-o":
                if (i + 1 < args.Length) outRoot = args[++i];
                break;
            case "--profile" or "-p":
                if (i + 1 < args.Length) profile = args[++i];
                break;
            case "--report-format" or "-r":
                if (i + 1 < args.Length) reportFormat = args[++i];
                break;
            case "--list-profiles":
                listProfiles = true;
                break;
            case "-h" or "--help" or "/?":
                help = true;
                break;
            default:
                targets.Add(args[i]);
                break;
        }
    }
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

        Beispiele:
          volvosplit C:\Dumps\ecu_Micro.mpc
          volvosplit C:\Dumps --out C:\Ausgabe
          volvosplit ecu.ori --profile tc1797
        """);
}
