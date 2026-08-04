using VolvoSplitter.Cli;

namespace VolvoSplitter.Core.Tests;

/// <summary>
/// Die Kommandozeile ist die einzige Bedienung des Stapelbetriebs, und ein
/// Zerlegewerkzeug für Auslesungen darf zweierlei nie tun: eine Option
/// stillschweigend übergehen und ein Ergebnis stillschweigend durch ein anderes
/// ersetzen. Diese Tests halten beides fest.
/// </summary>
public class CliCommandLineTests
{
    // -----------------------------------------------------------------------
    // Fehlende und falsch gesetzte Optionswerte
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("--out")]
    [InlineData("-o")]
    [InlineData("--profile")]
    [InlineData("-p")]
    [InlineData("--report-format")]
    [InlineData("-r")]
    public void OptionWithoutValue_IsAnError(string option)
    {
        // Vorher wortlos übergangen: „-o" ohne Ordner schrieb neben das Abbild,
        // „-p" ohne Namen ließ genau die Erkennung laufen, die übersteuert
        // werden sollte — beides mit Rückgabewert 0.
        Assert.False(CommandLine.TryParse(["probe.bin", option], out _, out string? error));
        Assert.NotNull(error);
        Assert.Contains(option, error);
    }

    [Fact]
    public void OptionFollowedByOption_IsAnError()
    {
        // „-o -f" legte einen Ordner namens „-f" an und ließ --fixed fallen.
        Assert.False(CommandLine.TryParse(["probe.bin", "-o", "-f"], out _, out string? error));
        Assert.NotNull(error);
        Assert.Contains("-f", error);
    }

    [Fact]
    public void EmptyOptionValue_IsAnError()
    {
        // Ein leerer Zielordner ist kein Zielordner, sondern ein Vertipper; er
        // landete sonst im Arbeitsverzeichnis.
        Assert.False(CommandLine.TryParse(["probe.bin", "--out", "  "], out _, out _));
    }

    [Fact]
    public void UnknownOption_IsAnError()
    {
        // Ein Vertipper an einer Option war ein nicht existierender Dateiname:
        // eine Zeile auf der Fehlerausgabe, Rückgabewert 0, und der Lauf lief
        // ohne die Option weiter.
        Assert.False(CommandLine.TryParse(["probe.bin", "--fixd"], out _, out string? error));
        Assert.NotNull(error);
        Assert.Contains("--fixd", error);
    }

    [Fact]
    public void DoubleDash_MakesEverythingAFileName()
    {
        // Der Ausweg für Abbilder, die wie eine Option heißen — ohne ihn wäre
        // die Prüfung auf unbekannte Optionen eine Sackgasse.
        Assert.True(CommandLine.TryParse(["--fixed", "--", "-p", "--out"], out var options, out _));

        Assert.True(options.FixedOnly);
        Assert.Equal(["-p", "--out"], options.Targets);
        Assert.Null(options.OutRoot);
    }

    [Fact]
    public void SingleDash_IsAFileName()
    {
        // Ein einzelner Bindestrich ist ein gültiger Dateiname.
        Assert.True(CommandLine.TryParse(["-"], out var options, out _));
        Assert.Equal(["-"], options.Targets);
    }

    [Fact]
    public void AllOptions_AreRead()
    {
        Assert.True(CommandLine.TryParse(
            ["a.bin", "--fixed", "--out", "ziel", "-p", "tc1797", "-r", "html", "b.ori"],
            out var options, out _));

        Assert.Equal(["a.bin", "b.ori"], options.Targets);
        Assert.True(options.FixedOnly);
        Assert.Equal("ziel", options.OutRoot);
        Assert.Equal("tc1797", options.Profile);
        Assert.Equal("html", options.ReportFormat);
        Assert.False(options.Help);
        Assert.False(options.ListProfiles);
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    [InlineData("/?")]
    public void HelpSwitches_AreRecognised(string arg)
    {
        Assert.True(CommandLine.TryParse([arg], out var options, out _));
        Assert.True(options.Help);
    }

    // -----------------------------------------------------------------------
    // Dateisammlung
    // -----------------------------------------------------------------------

    [Fact]
    public void CaseDifferingNames_FollowTheFileSystem()
    {
        // Unter Linux sind „A.bin" und „a.bin" zwei Dateien. Sie über einen
        // Vergleich ohne Rücksicht auf Groß- und Kleinschreibung zu entdoppeln
        // hieß, eine der beiden nie zu lesen — ohne jede Meldung. Unter Windows
        // ist es dieselbe Datei, und dann ist genau eine richtig.
        using var dir = new TempDir();
        string upper = dir.Write("A.bin", 1);
        string lower = dir.Write("a.bin", 2);

        var notes = new List<string>();
        var files = CommandLine.CollectFiles([upper, lower], notes);

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            Assert.Single(files);
            Assert.Single(notes);
        }
        else
        {
            Assert.Equal(2, files.Count);
            Assert.Empty(notes);
        }
    }

    [Fact]
    public void SameFileTwice_IsProcessedOnce_AndSaidSo()
    {
        // Entdoppelt wird über den vollen Pfad, nicht über die Schreibweise —
        // sonst wollten „probe.bin" und „./probe.bin" denselben Zielordner.
        using var dir = new TempDir();
        string file = dir.Write("probe.bin", 1);
        string spelledDifferently = Path.Combine(dir.Path, ".", "probe.bin");

        var notes = new List<string>();
        var files = CommandLine.CollectFiles([file, spelledDifferently], notes);

        Assert.Single(files);
        Assert.Single(notes);
        Assert.Contains("Doppelt genannt", notes[0]);
    }

    [Fact]
    public void MissingTarget_IsReported()
    {
        var notes = new List<string>();
        var files = CommandLine.CollectFiles([Path.Combine(Path.GetTempPath(), "gibtsnicht_" + Guid.NewGuid().ToString("N"))], notes);

        Assert.Empty(files);
        Assert.Single(notes);
        Assert.Contains("Nicht gefunden", notes[0]);
    }

    [Fact]
    public void Directory_IsReadOnlyAtTopLevel()
    {
        using var dir = new TempDir();
        dir.Write("eins.bin", 1);
        dir.Write("zwei.ori", 2);
        dir.Write("drei.txt", 3);
        Directory.CreateDirectory(Path.Combine(dir.Path, "tiefer"));
        File.WriteAllBytes(Path.Combine(dir.Path, "tiefer", "vier.bin"), [4]);

        var files = CommandLine.CollectFiles([dir.Path], []);

        Assert.Equal(2, files.Count);
        Assert.DoesNotContain(files, f => Path.GetFileName(f) == "vier.bin");
    }

    // -----------------------------------------------------------------------
    // Zielordner
    // -----------------------------------------------------------------------

    [Fact]
    public void SameNameFromTwoFolders_DoesNotShareOneTargetFolder()
    {
        // Der Befund: zwei verschiedene Abbilder namens „gleich.bin" legten
        // ihre Sektoren in denselben Ordner, und die eine bericht.txt beschrieb
        // nur das zuletzt zerlegte. Bei gleicher Teilenummer wurde byteweise
        // überschrieben — lautlos.
        var planner = new TargetDirectoryPlanner();

        string first = planner.Claim(Path.Combine("a", "gleich.bin"), "a", "gleich.bin", "out", out string? noteA);
        string second = planner.Claim(Path.Combine("b", "gleich.bin"), "b", "gleich.bin", "out", out string? noteB);

        Assert.NotEqual(first, second);
        Assert.Null(noteA);
        Assert.NotNull(noteB);
        // Der Elternordner benennt, welches Abbild gemeint ist; eine laufende
        // Nummer sagte nur, dass es das zweite war.
        Assert.Equal(Path.Combine("out", "b_gleich"), second);
    }

    [Fact]
    public void EqualParentNames_FallBackToACounter()
    {
        // Unterscheidet auch der Elternordner nicht, bleibt nur die Nummer —
        // ausweichen muss trotzdem gelingen, sonst stirbt der Stapellauf.
        var planner = new TargetDirectoryPlanner();
        string a = Path.Combine("eins", "x", "gleich.bin");
        string b = Path.Combine("zwei", "x", "gleich.bin");
        string c = Path.Combine("drei", "x", "gleich.bin");

        Assert.Equal(Path.Combine("out", "gleich"), planner.Claim(a, "x", "gleich.bin", "out", out _));
        Assert.Equal(Path.Combine("out", "x_gleich"), planner.Claim(b, "x", "gleich.bin", "out", out _));
        Assert.Equal(Path.Combine("out", "gleich_2"), planner.Claim(c, "x", "gleich.bin", "out", out _));
    }

    [Fact]
    public void WithoutOutRoot_TheFolderKeepsItsSuffix()
    {
        var planner = new TargetDirectoryPlanner();

        string dir = planner.Claim(Path.Combine("a", "ecu.mpc"), "a", "ecu.mpc", null, out string? note);

        Assert.Equal(Path.Combine("a", "ecu_sektoren"), dir);
        Assert.Null(note);
    }

    [Fact]
    public void DifferentNames_KeepTheirOwnFolders()
    {
        // Ausgewichen wird nur bei echter Kollision — sonst wären alle Ordner
        // nach dem ersten Lauf anders benannt als dokumentiert.
        var planner = new TargetDirectoryPlanner();

        Assert.Equal(Path.Combine("out", "eins"), planner.Claim("eins.bin", ".", "eins.bin", "out", out _));
        Assert.Equal(Path.Combine("out", "zwei"), planner.Claim("zwei.bin", ".", "zwei.bin", "out", out _));
    }

    /// <summary>Ein Ordner unter dem Temp-Pfad, der sich selbst wieder abräumt.</summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vs_cli_" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        /// <summary>Legt eine Datei mit einem unterscheidbaren Byte an.</summary>
        public string Write(string name, byte content)
        {
            string full = System.IO.Path.Combine(Path, name);
            File.WriteAllBytes(full, [content]);
            return full;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
