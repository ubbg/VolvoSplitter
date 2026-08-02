using VolvoSplitter.Core;

// ---------------------------------------------------------------------------
// Stapelbetrieb ohne Oberfläche: zerlegt Flash-Abbilder, schreibt jeden Sektor
// als Rohdatei und legt einen Textbefund daneben. Möglich, weil die gesamte
// Analyse in VolvoSplitter.Core ohne WPF-Abhängigkeit steckt.
//
//   volvosplit <Datei|Ordner> [weitere...] [--fixed] [--out <Ordner>]
// ---------------------------------------------------------------------------

ParseArgs(args, out var targets, out bool fixedOnly, out string? outRoot, out bool help);

if (help || targets.Count == 0)
{
    PrintUsage();
    return help ? 0 : 1;
}

var files = CollectFiles(targets);
if (files.Count == 0)
{
    Console.Error.WriteLine("Keine Abbilder gefunden (.mpc / .bin).");
    return 1;
}

int ok = 0;
foreach (string file in files)
{
    try
    {
        Process(file, fixedOnly, outRoot);
        ok++;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
    {
        Console.Error.WriteLine($"Fehler bei {file}: {ex.Message}");
    }
}

Console.WriteLine();
Console.WriteLine($"{ok}/{files.Count} Abbilder verarbeitet.");
return ok == files.Count ? 0 : 1;

// ---------------------------------------------------------------------------

static void Process(string path, bool fixedOnly, string? outRoot)
{
    var dump = FlashDump.Load(path, fixedOnly);

    string targetDir = outRoot is null
        ? Path.Combine(dump.Directory, Path.GetFileNameWithoutExtension(dump.FileName) + "_sektoren")
        : Path.Combine(outRoot, Path.GetFileNameWithoutExtension(dump.FileName));
    Directory.CreateDirectory(targetDir);

    Console.WriteLine($"{dump.FileName}  ·  {FlashFormat.FamilyName(dump.Family)}  ·  " +
                      $"{dump.Sectors.Count(s => s.Present)} Sektoren");

    foreach (var sector in dump.Sectors.Where(s => s.Present))
    {
        string outPath = dump.ExtractSector(sector, targetDir);
        string flag = sector.CrcOk ? "ok  " : "CRC!";
        Console.WriteLine($"   {flag}  {sector.Label,-14} {sector.PartNumber,-12} " +
                          $"{sector.SizeText,12}  ->  {Path.GetFileName(outPath)}");
    }

    string reportPath = Path.Combine(targetDir, "bericht.txt");
    File.WriteAllText(reportPath, DumpReport.Build(dump));
    Console.WriteLine($"   Bericht  ->  {Path.GetFileName(reportPath)}");
}

static List<string> CollectFiles(List<string> targets)
{
    var files = new List<string>();
    foreach (string target in targets)
    {
        if (Directory.Exists(target))
        {
            files.AddRange(Directory.EnumerateFiles(target, "*.mpc", SearchOption.TopDirectoryOnly));
            files.AddRange(Directory.EnumerateFiles(target, "*.bin", SearchOption.TopDirectoryOnly));
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
                      out string? outRoot, out bool help)
{
    targets = [];
    fixedOnly = false;
    outRoot = null;
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
            case "-h" or "--help" or "/?":
                help = true;
                break;
            default:
                targets.Add(args[i]);
                break;
        }
    }
}

static void PrintUsage()
{
    Console.WriteLine("""
        Volvo File Splitter — Stapelbetrieb

          volvosplit <Datei|Ordner> [weitere...] [Optionen]

        Zerlegt jedes Flash-Abbild in seine Sektoren, schreibt sie als Rohdateien
        und legt einen Textbefund daneben.

        Optionen:
          -f, --fixed         Nur die fest verdrahteten Standardadressen lesen
          -o, --out <Ordner>  Zielordner (Standard: neben dem Abbild)
          -h, --help          Diese Hilfe

        Beispiele:
          volvosplit C:\Dumps\ecu_Micro.mpc
          volvosplit C:\Dumps --out C:\Ausgabe
        """);
}
