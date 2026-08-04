namespace VolvoSplitter.Cli;

// ---------------------------------------------------------------------------
// Aus der Argumentliste wird hier ein Arbeitsplan: was ist gemeint, welche
// Dateien sind es wirklich, und wohin darf jede schreiben. Das steht neben dem
// Programmrumpf, weil es Entscheidungen trifft — fehlender Optionswert,
// unbekannte Option, belegter Zielordner — und Entscheidungen gehören geprüft.
// Ein- und Ausgabe bleiben drüben: hier wird nichts auf die Konsole geschrieben,
// Meldungen werden zurückgegeben.
// ---------------------------------------------------------------------------

/// <summary>Die zerlegte Kommandozeile.</summary>
public sealed class CommandLineOptions
{
    /// <summary>Dateien und Ordner, die zerlegt werden sollen.</summary>
    public List<string> Targets { get; } = [];

    /// <summary>Nur die fest verdrahteten Standardadressen lesen.</summary>
    public bool FixedOnly { get; set; }

    /// <summary>Gemeinsamer Zielordner, oder <c>null</c> für „neben dem Abbild“.</summary>
    public string? OutRoot { get; set; }

    /// <summary>Erzwungenes Profil, oder <c>null</c> für die Erkennung.</summary>
    public string? Profile { get; set; }

    /// <summary>Berichtsformat als Rohtext; die Prüfung macht der Aufrufer.</summary>
    public string? ReportFormat { get; set; }

    /// <summary>Nur die Profilliste ausgeben.</summary>
    public bool ListProfiles { get; set; }

    /// <summary>Nur die Hilfe ausgeben.</summary>
    public bool Help { get; set; }
}

/// <summary>Zerlegt die Argumentliste und sammelt die Abbilder ein.</summary>
public static class CommandLine
{
    /// <summary>
    /// Vergleicht Pfade so, wie das Dateisystem sie vergleicht: Windows und
    /// macOS unterscheiden in ihrer Vorgabe keine Groß- und Kleinschreibung,
    /// Linux tut es. Pauschal ohne Rücksicht zu vergleichen hieße unter Linux,
    /// <c>A.bin</c> und <c>a.bin</c> für dieselbe Datei zu halten und eine der
    /// beiden nie zu lesen — Datenverlust ohne Meldung. Umgekehrt wäre ein
    /// pauschal genauer Vergleich unter Windows eine doppelte Zerlegung
    /// derselben Datei. Abweichend konfigurierte Dateisysteme (Windows-Ordner
    /// mit Groß-/Kleinschreibung, case-sensitives APFS) trifft die Vorgabe
    /// nicht; sie zuverlässig zu erkennen ginge nur über einen Schreibversuch
    /// je Ordner, und das ist für ein Zerlegewerkzeug der falsche Preis.
    /// </summary>
    public static StringComparer PathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    /// <summary>Dateiendungen, die als Abbild gelten.</summary>
    private static readonly string[] Patterns = ["*.mpc", "*.bin", "*.ori"];

    /// <summary>
    /// Zerlegt die Argumentliste. Liefert <c>false</c> mit Meldung, sobald der
    /// Aufruf selbst falsch ist — ein fehlender Optionswert ist ein Vertipper
    /// und keine Vorgabe. Vorher wurde er wortlos übergangen: <c>-o</c> ohne
    /// Ordner schrieb neben das Abbild, <c>-p</c> ohne Namen ließ die Erkennung
    /// laufen, die der Nutzer gerade übersteuern wollte.
    /// </summary>
    public static bool TryParse(string[] args, out CommandLineOptions options, out string? error)
    {
        options = new CommandLineOptions();
        error = null;

        // Hinter „--" ist alles ein Dateiname. Das ist der Ausweg für Abbilder,
        // die wie eine Option heißen — ohne ihn wäre die Prüfung auf unbekannte
        // Optionen eine Sackgasse.
        bool onlyTargets = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            if (onlyTargets)
            {
                options.Targets.Add(arg);
                continue;
            }

            switch (arg)
            {
                case "--":
                    onlyTargets = true;
                    break;
                case "--fixed" or "-f":
                    options.FixedOnly = true;
                    break;
                case "--out" or "-o":
                    if (!TryTakeValue(args, ref i, "einen Ordner", out string? outRoot, out error)) return false;
                    options.OutRoot = outRoot;
                    break;
                case "--profile" or "-p":
                    if (!TryTakeValue(args, ref i, "einen Profilnamen", out string? profile, out error)) return false;
                    options.Profile = profile;
                    break;
                case "--report-format" or "-r":
                    if (!TryTakeValue(args, ref i, "ein Format", out string? format, out error)) return false;
                    options.ReportFormat = format;
                    break;
                case "--list-profiles":
                    options.ListProfiles = true;
                    break;
                case "-h" or "--help" or "/?":
                    options.Help = true;
                    break;
                default:
                    // Ein Vertipper an einer Option („--fixd") wäre sonst ein
                    // Dateiname, der nicht existiert: eine Zeile auf der
                    // Fehlerausgabe, Rückgabewert 0 — und der Lauf liefe ohne
                    // die Option weiter, die der Nutzer gesetzt zu haben glaubt.
                    if (LooksLikeOption(arg))
                    {
                        error = $"Unbekannte Option „{arg}\" — „volvosplit --help\" zeigt die erlaubten. " +
                                "Ein Abbild, das so heißt, steht hinter „--\".";
                        return false;
                    }
                    options.Targets.Add(arg);
                    break;
            }
        }

        return true;
    }

    /// <summary>
    /// Holt den Wert hinter einer Option. Ein fehlender, leerer oder wie eine
    /// Option aussehender Wert ist ein Aufruffehler: <c>-o -f</c> legte sonst
    /// einen Ordner namens „-f“ an und ließ <c>--fixed</c> unter den Tisch fallen.
    /// </summary>
    private static bool TryTakeValue(string[] args, ref int i, string expectation,
                                     out string? value, out string? error)
    {
        string option = args[i];
        value = null;
        error = null;

        if (i + 1 >= args.Length)
        {
            error = $"„{option}\" erwartet {expectation}, es folgt aber nichts mehr.";
            return false;
        }

        string candidate = args[i + 1];

        if (string.IsNullOrWhiteSpace(candidate))
        {
            error = $"„{option}\" erwartet {expectation}, der Wert ist aber leer.";
            return false;
        }

        if (LooksLikeOption(candidate))
        {
            error = $"„{option}\" erwartet {expectation}, gefunden wurde die Option „{candidate}\". " +
                    $"Wenn der Wert wirklich so heißt: „.{Path.DirectorySeparatorChar}{candidate}\" schreiben.";
            return false;
        }

        value = candidate;
        i++;
        return true;
    }

    /// <summary>
    /// Ein einzelner Bindestrich ist ein gültiger Dateiname und keine Option;
    /// alles Längere mit Bindestrich am Anfang ist als Option gemeint.
    /// </summary>
    private static bool LooksLikeOption(string arg) => arg.Length > 1 && arg[0] == '-';

    /// <summary>
    /// Löst Ziele in Abbilddateien auf. Ordner werden nur auf oberster Ebene
    /// gelesen. Doppelt genannte Dateien werden einmal verarbeitet — welche
    /// Pfade als dieselbe Datei gelten, entscheidet <see cref="PathComparer"/>,
    /// und jede verworfene Nennung wird gemeldet: eine stillschweigend
    /// übersprungene Auslesung ist genau das, was ein Zerlegewerkzeug nicht
    /// tun darf.
    /// </summary>
    public static List<string> CollectFiles(IEnumerable<string> targets, List<string> notes)
    {
        var files = new List<string>();
        var seen = new HashSet<string>(PathComparer);

        foreach (string target in targets)
        {
            if (Directory.Exists(target))
            {
                foreach (string pattern in Patterns)
                    foreach (string file in Directory.EnumerateFiles(target, pattern, SearchOption.TopDirectoryOnly))
                        Take(file);
            }
            else if (File.Exists(target))
            {
                Take(target);
            }
            else
            {
                notes.Add($"Nicht gefunden: {target}");
            }
        }

        return files;

        // Entdoppelt über den vollen Pfad, nicht über die Schreibweise: sonst
        // gälten „probe.bin" und „./probe.bin" als zwei Abbilder, und beide
        // wollten denselben Zielordner.
        void Take(string path)
        {
            if (seen.Add(Path.GetFullPath(path)))
                files.Add(path);
            else
                notes.Add($"Doppelt genannt, einmal verarbeitet: {path}");
        }
    }
}

/// <summary>
/// Vergibt die Zielordner eines Laufs und merkt sich, wer welchen belegt hat.
/// </summary>
/// <remarks>
/// Der Ordnername kam allein aus dem Dateinamen ohne Endung, und
/// <c>Directory.CreateDirectory</c> ist bei vorhandenem Ordner ein No-op: zwei
/// gleichnamige Abbilder aus verschiedenen Ordnern schrieben ihre Sektoren
/// nebeneinander in denselben Ordner, und die eine <c>bericht.txt</c>
/// beschrieb nur das zuletzt zerlegte. Bei gleicher Teilenummer wurde
/// byteweise überschrieben — lautlos. Bei Auslesungen heißen Dateien reihenweise
/// <c>Original.bin</c> oder <c>Read.bin</c>; das ist kein Sonderfall.
///
/// Abgebrochen wird deshalb nicht: ein Stapellauf über hunderte Abbilder darf
/// nicht am zweiten Fund sterben. Stattdessen bekommt das zweite Abbild einen
/// eigenen Ordner — benannt nach seinem Elternordner, nicht nach einer laufenden
/// Nummer. Eine Nummer sagt „das zweite", der Elternordner sagt „welches"; für
/// eine Ablage, die als Beleg dienen soll, ist das der Unterschied zwischen
/// einer Kennung und einem Namen. Erst wenn auch der nicht unterscheidet, zählt
/// eine Nummer hoch.
///
/// Belegt heißt „in diesem Lauf vergeben", nicht „liegt schon auf der Platte":
/// derselbe Aufruf zweimal ausgeführt muss dieselben Ordner beschreiben, sonst
/// wüchse bei jedem Durchgang ein weiterer Satz Ordner heran.
/// </remarks>
public sealed class TargetDirectoryPlanner
{
    /// <summary>Vergebener Zielordner → Abbild, das ihn belegt hat.</summary>
    private readonly Dictionary<string, string> _claimed = new(CommandLine.PathComparer);

    /// <summary>
    /// Belegt den Zielordner für ein Abbild. <paramref name="note"/> trägt eine
    /// Meldung, wenn ausgewichen werden musste — der Nutzer muss erfahren, dass
    /// seine Ablage anders heißt als erwartet.
    /// </summary>
    /// <param name="sourcePath">Pfad des Abbilds, für die Meldung.</param>
    /// <param name="dumpDirectory">Ordner des Abbilds, für den Fall ohne <c>--out</c>.</param>
    /// <param name="fileName">Dateiname des Abbilds mit Endung.</param>
    /// <param name="outRoot">Gemeinsamer Zielordner, oder <c>null</c>.</param>
    /// <param name="note">Meldung oder <c>null</c>.</param>
    public string Claim(string sourcePath, string dumpDirectory, string fileName,
                        string? outRoot, out string? note)
    {
        note = null;

        string root = outRoot ?? dumpDirectory;
        string stem = Path.GetFileNameWithoutExtension(fileName);
        // Ohne --out trägt der Ordner seit jeher das Suffix, damit er neben dem
        // Abbild als Ablage erkennbar bleibt.
        string tail = outRoot is null ? "_sektoren" : string.Empty;

        string first = Path.Combine(root, stem + tail);
        if (_claimed.TryAdd(first, sourcePath)) return first;

        foreach (string candidate in Alternatives(root, stem, tail, sourcePath))
        {
            if (!_claimed.TryAdd(candidate, sourcePath)) continue;

            note = $"Zielordner „{Path.GetFileName(first)}\" ist schon von {_claimed[first]} belegt — " +
                   $"dieses Abbild geht nach „{Path.GetFileName(candidate)}\".";
            return candidate;
        }

        // Unerreichbar, solange die Nummernfolge offen ist; die Schleife braucht
        // trotzdem ein Ende, das der Übersetzer sieht.
        throw new InvalidOperationException($"Kein freier Zielordner für {sourcePath}.");
    }

    /// <summary>Erst der Elternordner als Unterscheidung, dann laufende Nummern.</summary>
    private static IEnumerable<string> Alternatives(string root, string stem, string tail, string sourcePath)
    {
        string parent = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? string.Empty);

        // Ein Abbild direkt auf einem Laufwerks- oder Wurzelpfad hat keinen
        // benennbaren Elternordner — dann bleiben nur die Nummern.
        if (parent.Length > 0)
            yield return Path.Combine(root, parent + "_" + stem + tail);

        for (int n = 2; ; n++)
            yield return Path.Combine(root, stem + "_" + n + tail);
    }
}
