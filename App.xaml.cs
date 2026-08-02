using System.Windows;

namespace VolvoSplitter;

public partial class App : Application
{
    /// <summary>Datei, die per Kommandozeile oder Ziehen auf die Anwendung übergeben wurde.</summary>
    public static string? StartupFile { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length > 0)
            StartupFile = e.Args[0];
        base.OnStartup(e);
    }
}
