using System.Windows;

namespace VolvoSplitter;

public partial class App : Application
{
    /// <summary>Datei, die per Kommandozeile oder Ziehen auf die Anwendung übergeben wurde.</summary>
    public static string? StartupFile { get; private set; }

    /// <summary>
    /// Zweite übergebene Datei. Sie wird als Quelle geöffnet — dieselbe
    /// Zuordnung wie beim Ablegen mehrerer Dateien im Fenster: die erste ist das
    /// Ziel, die zweite die Quelle.
    /// </summary>
    public static string? StartupSource { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length > 0) StartupFile = e.Args[0];
        if (e.Args.Length > 1) StartupSource = e.Args[1];
        base.OnStartup(e);
    }
}
