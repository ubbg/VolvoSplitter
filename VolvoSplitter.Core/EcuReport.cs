using System.IO;

namespace VolvoSplitter.Core;

/// <summary>
/// Die Protokolldatei, die das Auslesegerät neben den Dump legt
/// (gleicher Name ohne "_Micro", Endung .TXT). Enthält Steuergerät- und
/// Fahrzeugdaten, die nicht im Flash stehen.
/// </summary>
public sealed class EcuReport
{
    private readonly Dictionary<string, string> _fields = new(StringComparer.OrdinalIgnoreCase);

    public string Path { get; private init; } = "";

    public string this[string key] => _fields.TryGetValue(key, out var v) ? v : "";

    public string Plugin => this["Plugin"];
    public string Micro => this["Micro"];
    public string HardwareNumber => this["Hardware Nr."];
    public string SoftwareNumber => this["Software Nr."];
    public string ChassisNumber => this["Chassis Nr."];
    public string ConnectionMode => this["Connection Mode"];
    public string ReadDate => this["Date"];
    public string ReadTime => this["Time"];

    /// <summary>Die drei Teilenummern aus "Software Upgrade Nr.".</summary>
    public IReadOnlyList<string> UpgradeNumbers =>
        this["Software Upgrade Nr."].Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Sucht die Protokolldatei neben dem Dump.</summary>
    public static EcuReport? FindFor(string dumpPath)
    {
        string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(dumpPath)) ?? ".";
        string stem = System.IO.Path.GetFileNameWithoutExtension(dumpPath);

        foreach (string candidate in Candidates(stem))
        {
            string path = System.IO.Path.Combine(dir, candidate + ".TXT");
            if (File.Exists(path)) return Parse(path);
            path = System.IO.Path.Combine(dir, candidate + ".txt");
            if (File.Exists(path)) return Parse(path);
        }
        return null;
    }

    private static IEnumerable<string> Candidates(string stem)
    {
        yield return stem;
        foreach (string suffix in new[] { "_Micro", "_micro", "_ExtFlash", "_Eeprom" })
            if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                yield return stem[..^suffix.Length];
    }

    public static EcuReport? Parse(string path)
    {
        try
        {
            var report = new EcuReport { Path = path };
            foreach (string line in File.ReadAllLines(path))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string key = line[..colon].Trim();
                string value = line[(colon + 1)..].Trim();
                if (key.Length > 0 && value.Length > 0)
                    report._fields[key] = value;
            }
            return report._fields.Count > 0 ? report : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
