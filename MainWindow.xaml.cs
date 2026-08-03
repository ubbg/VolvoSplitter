using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using VolvoSplitter.Core;
using VolvoSplitter.Core.TriCore;
using VolvoSplitter.Native;

namespace VolvoSplitter;

public partial class MainWindow : Window
{
    private FlashDump? _dump;

    public MainWindow()
    {
        InitializeComponent();
        WindowTheme.ApplyDark(this);

        Map.SectorHovered += (_, sector) => Map.Highlighted = sector;
        Map.SectorClicked += (_, sector) => ScrollToSector(sector);

        Loaded += (_, _) =>
        {
            if (App.StartupFile is { } path && File.Exists(path))
                LoadFile(path);
        };
    }

    private bool AnimationsEnabled => SystemParameters.ClientAreaAnimation;

    /// <summary>Nur die fest verdrahteten Adressen lesen statt das Abbild zu durchsuchen.</summary>
    private bool FixedOnly => ScanToggle.IsChecked == true;

    /// <summary>Beim Schließen bei ungesicherten Änderungen nachfragen.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmDiscardChanges()) e.Cancel = true;
        base.OnClosing(e);
    }

    // ==================================================================
    // Datei laden
    // ==================================================================

    private void LoadFile(string path)
    {
        if (!ConfirmDiscardChanges()) return;

        try
        {
            _dump = FlashDump.Load(path, FixedOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowProblem("Datei lässt sich nicht lesen", ex.Message);
            return;
        }
        catch (OutOfMemoryException)
        {
            ShowProblem("Datei ist zu groß",
                        "Das Abbild passt nicht in den Speicher. Erwartet werden einige Megabyte.");
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;
        LoadedState.Visibility = Visibility.Visible;
        Refresh();

        if (AnimationsEnabled)
            LoadedState.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    private void Refresh()
    {
        if (_dump is null) return;

        FileNameText.Text = _dump.FileName;
        FileMetaText.Text = $"{_dump.Size:N0} B  ·  {Hex.Addr(_dump.Size)}  ·  " +
                            $"{_dump.Profile.FamilyName}  ·  {_dump.Profile.MicroName}  ·  " +
                            _dump.Profile.Manufacturer;

        ModifiedBadge.Visibility = _dump.IsModified ? Visibility.Visible : Visibility.Collapsed;
        SaveDumpButton.Visibility = _dump.IsModified && Writable
            ? Visibility.Visible : Visibility.Collapsed;

        UpdateDetectionBadges();

        UpdateIdentity();

        SectorList.ItemsSource = null;
        SectorList.ItemsSource = _dump.Sectors;

        RegionList.ItemsSource = null;
        RegionList.ItemsSource = _dump.Regions;
        RegionSection.Visibility = _dump.Regions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        PartitionList.ItemsSource = null;
        PartitionList.ItemsSource = _dump.Partitions;
        PartitionSection.Visibility = _dump.Partitions.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;

        // Die Herkunft der Blockkarte steht nicht mehr fest im Text, sondern
        // kommt aus dem Layout selbst.
        LayoutSourceText.Text = _dump.Layout is { } layout
            ? layout.SourceNote + (layout.Complete ? "" : " — Zuordnung nicht vollständig")
            : "";

        Map.ImageSize = _dump.Size;
        Map.Sectors = _dump.Sectors;
        Map.Regions = _dump.Regions;
        Map.Highlighted = null;

        UpdateStatusLine();
        AnimateCards();
    }

    /// <summary>Das erkannte Profil sieht Zurückschreiben vor.</summary>
    private bool Writable => _dump?.Profile.SupportsWriteBack == true;

    /// <summary>
    /// Zwei Abzeichen neben dem Dateinamen: eine knappe Erkennung wird als
    /// „nicht eindeutig" ausgewiesen statt stillschweigend entschieden, und ein
    /// Profil ohne Zurückschreiben sagt das offen.
    /// </summary>
    private void UpdateDetectionBadges()
    {
        if (_dump is null) return;

        var detection = _dump.Detection;
        bool unsure = detection.Ambiguous || detection.DeviceAmbiguous;

        AmbiguousBadge.Visibility = unsure ? Visibility.Visible : Visibility.Collapsed;
        AmbiguousText.Text = detection.DeviceAmbiguous && detection.DeviceCandidates.Count > 0
            ? "Baustein offen: " + string.Join(" oder ", detection.DeviceCandidates)
            : "Zuordnung nicht eindeutig";
        AmbiguousBadge.ToolTip = string.Join("\n", detection.Evidence);

        ReadOnlyBadge.Visibility = Writable ? Visibility.Collapsed : Visibility.Visible;
        ReadOnlyBadge.ToolTip = Writable
            ? null
            : $"Für {_dump.Profile.FamilyName} werden Prüfsummen gerechnet und gemeldet, " +
              "aber nicht gestellt. Speichern, Korrigieren und Ersetzen sind deshalb abgeschaltet.";
    }

    private void UpdateIdentity()
    {
        if (_dump is null) return;

        var vehicle = _dump.Vehicle;
        var report = _dump.Report;
        var identity = _dump.Identity;

        if (vehicle is null && report is null && identity is null)
        {
            IdentityPanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (identity is not null && vehicle is null)
        {
            UpdateBoschIdentity(identity);
            return;
        }

        IdentityPanel.Visibility = Visibility.Visible;
        VinText.Text = vehicle?.Vin ?? "Fahrzeugnummer nicht im Abbild";

        var vehicleParts = new List<string>();
        if (vehicle is not null) vehicleParts.Add($"Fahrgestell {vehicle.ChassisNumber}");
        if (report is not null)
        {
            if (report.HardwareNumber.Length > 0) vehicleParts.Add($"Hardware {report.HardwareNumber}");
            if (report.SoftwareNumber.Length > 0) vehicleParts.Add($"Software {report.SoftwareNumber}");
        }
        VehicleMetaText.Text = string.Join("   ·   ", vehicleParts);
        VehicleMetaText.Visibility = vehicleParts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var ecuParts = new List<string>();
        if (report is not null)
        {
            if (report.Plugin.Length > 0) ecuParts.Add(report.Plugin);
            if (report.Micro.Length > 0) ecuParts.Add(report.Micro);
            if (report.ReadDate.Length > 0) ecuParts.Add($"ausgelesen {report.ReadDate}");
            ecuParts.Add($"Protokoll {Path.GetFileName(report.Path)}");
        }
        EcuMetaText.Text = string.Join("   ·   ", ecuParts);
        EcuMetaText.Visibility = ecuParts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Kennungen eines Bosch-Abbilds in dieselben Textblöcke — mit Fundort,
    /// denn der ist der Beleg. Die Zeile aus der Steuergerätetabelle wird als
    /// Nutzerangabe gekennzeichnet, nicht als Herstellerdatum.
    /// </summary>
    private void UpdateBoschIdentity(BoschIdentity identity)
    {
        IdentityPanel.Visibility = Visibility.Visible;

        VinText.Text = identity.VehicleNumber ?? identity.EcuType ?? "Steuergerät nicht benannt";

        VehicleMetaText.Text = string.Join("   ·   ",
            identity.Hits.Where(h => h.Kind is "Hardware" or "Software" or "Teilenummer"
                                            or "VAG-Software" or "Softwarestand" or "VAG-Hardware")
                         .Select(h => $"{h.Kind} {h.Value} bei {Hex.Addr(h.Offset)}"));
        VehicleMetaText.Visibility = VehicleMetaText.Text.Length > 0
            ? Visibility.Visible : Visibility.Collapsed;

        var parts = new List<string>();

        // Teilenummer und Stand zusammen, wie sie auf dem Steuergerät stehen —
        // das ist die Angabe, nach der ein Diagnosetester fragt.
        if (identity.Vag is { } vag)
        {
            parts.Add(vag.SoftwareText);
            if (vag.EngineText is { } engine) parts.Add(engine);
            if (vag.EngineCodes.Count > 0) parts.Add($"MKB {vag.EngineCodeText}");
        }

        if (VagEcuCatalog.Find(identity.EcuType) is { } entry)
            parts.Add(entry.Display + " (Angabe aus der Steuergerätetabelle)");
        if (_dump?.Chain.Variant is { } variant)
            parts.Add($"Variante {variant}");
        if (_dump?.Chain.Cvn is { } cvn)
            parts.Add($"CVN {cvn.ValueText}");

        EcuMetaText.Text = string.Join("   ·   ", parts);
        EcuMetaText.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateStatusLine(string? message = null)
    {
        if (_dump is null) return;

        if (message is not null)
        {
            StatusLine.Text = message;
            return;
        }

        int found = _dump.Sectors.Count(s => s.Present);
        int mismatched = _dump.Sectors.Count(s => s.Status == SectorStatus.CrcMismatch);
        int missing = _dump.Sectors.Count(s => !s.Present);

        var parts = new List<string> { $"{found} Sektoren gefunden" };
        if (mismatched > 0) parts.Add($"{mismatched} × Prüfsumme weicht ab");
        if (missing > 0) parts.Add($"{missing} nicht gefunden");
        if (_dump.Regions.Count > 0) parts.Add($"{_dump.Regions.Count} Bereiche ohne Kopf");

        StatusLine.Text = string.Join("   ·   ", parts);
        ExtractAllButton.IsEnabled = found > 0;
        RepairAllButton.Visibility = mismatched > 0 && Writable
            ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Karten laufen versetzt ein — ein Vorgang, nicht fünf Effekte.</summary>
    private void AnimateCards()
    {
        if (!AnimationsEnabled) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            for (int i = 0; i < SectorList.Items.Count; i++)
            {
                if (SectorList.ItemContainerGenerator.ContainerFromIndex(i) is not UIElement container)
                    continue;

                var slide = new TranslateTransform(0, 8);
                container.RenderTransform = slide;
                container.Opacity = 0;

                var delay = TimeSpan.FromMilliseconds(40 * i);
                container.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1,
                    TimeSpan.FromMilliseconds(200)) { BeginTime = delay });
                slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0,
                    TimeSpan.FromMilliseconds(220))
                { BeginTime = delay, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ==================================================================
    // Datei öffnen, Ziehen und Ablegen
    // ==================================================================

    private void OnOpenClick(object sender, RoutedEventArgs e) => PickFile();

    private void PickFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Flash-Abbild wählen",
            Filter = "Flash-Abbilder (*.mpc;*.bin;*.ori)|*.mpc;*.bin;*.ori|Alle Dateien (*.*)|*.*",
            CheckFileExists = true
        };
        if (_dump is not null) dialog.InitialDirectory = _dump.Directory;
        if (dialog.ShowDialog(this) == true) LoadFile(dialog.FileName);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool acceptable = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = acceptable ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;

        if (acceptable && EmptyState.Visibility == Visibility.Visible)
        {
            DropZone.BorderBrush = (Brush)FindResource("SignalBrush");
            DropHint.Text = "Loslassen zum Laden";
        }
    }

    private void OnDragLeave(object sender, DragEventArgs e) => ResetDropZone();

    private void ResetDropZone()
    {
        DropZone.BorderBrush = (Brush)FindResource("LineBrush");
        DropHint.Text = "Flash-Abbild hierher ziehen";
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        ResetDropZone();
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            LoadFile(files[0]);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyboardDevice.Modifiers != ModifierKeys.Control) return;

        switch (e.Key)
        {
            case Key.O:
                PickFile();
                e.Handled = true;
                break;
            case Key.S when _dump?.IsModified == true && Writable:
                SaveDump();
                e.Handled = true;
                break;
            case Key.E when _dump is not null:
                ExtractAll();
                e.Handled = true;
                break;
        }
    }

    // ==================================================================
    // Sektoraktionen
    // ==================================================================

    private static SectorInfo? SectorOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as SectorInfo;

    private void OnExtractSectorClick(object sender, RoutedEventArgs e)
    {
        if (_dump is null || SectorOf(sender) is not { } sector) return;

        try
        {
            string path = _dump.ExtractSector(sector);
            sector.Note = sector.CrcOk
                ? $"Geschrieben nach {path}"
                : $"Geschrieben nach {path} — Prüfsumme weicht ab, Inhalt unverändert übernommen";
            UpdateStatusLine($"{sector.OutputName} geschrieben");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowProblem("Sektor lässt sich nicht schreiben", ex.Message);
        }
    }

    private void OnExportSRecordClick(object sender, RoutedEventArgs e)
    {
        if (_dump is null || SectorOf(sender) is not { } sector) return;

        var dialog = new SaveFileDialog
        {
            Title = "Als S-Record speichern",
            FileName = sector.OutputName + ".s3",
            Filter = "Motorola S-Record (*.s3)|*.s3|Alle Dateien (*.*)|*.*",
            InitialDirectory = _dump.Directory
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            _dump.ExportSRecord(sector, dialog.FileName);
            sector.Note = $"S-Record geschrieben nach {dialog.FileName}  " +
                          $"(Ladeadresse 0x{sector.CpuOffset:X6})";
            UpdateStatusLine($"{Path.GetFileName(dialog.FileName)} geschrieben");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowProblem("S-Record lässt sich nicht schreiben", ex.Message);
        }
    }

    private void OnRepairCrcClick(object sender, RoutedEventArgs e)
    {
        if (_dump is null || !Writable || SectorOf(sender) is not { } sector) return;

        var repair = _dump.RepairCrc(sector);
        _dump.Analyze(FixedOnly);

        var repaired = _dump.Sectors.FirstOrDefault(s => s.Start == sector.Start);
        if (repaired is not null)
        {
            string note = $"Prüfsumme korrigiert: 0x{repair.OldCrc:X8} → 0x{repair.NewCrc:X8}. " +
                          "Der Dump ist geändert und noch nicht gespeichert.";

            if (repair.StaleCopies.Count > 0)
                note += "\nDer alte Wert steht weiterhin bei " +
                        string.Join(", ", repair.StaleCopies.Select(a => $"0x{a:X6}")) +
                        ". Das Steuergerät hält dort Kopien.";

            repaired.Note = note;
        }

        Refresh();
        UpdateStatusLine(repair.StaleCopies.Count > 0
            ? $"Prüfsumme korrigiert — {repair.StaleCopies.Count} alte Kopie(n) im Abbild"
            : "Prüfsumme korrigiert — Dump speichern nicht vergessen");

        if (repair.StaleCopies.Count > 0) OfferCopyUpdate(repair);
    }

    /// <summary>
    /// Fragt, ob die Kopien des alten Prüfwerts mitgezogen werden sollen. Ob
    /// das Steuergerät sie prüft, lässt sich aus dem Abbild nicht sagen —
    /// deshalb ist es eine Entscheidung des Anwenders, keine Automatik.
    /// </summary>
    private void OfferCopyUpdate(FlashDump.CrcRepair repair)
    {
        if (_dump is null) return;

        string where = string.Join("\n   ", repair.StaleCopies.Select(a => $"0x{a:X6}"));

        string question =
            $"Der alte Prüfwert 0x{repair.OldCrc:X8} steht noch an {repair.StaleCopies.Count} " +
            $"weiteren Stellen:\n\n   {where}\n\n" +
            "Das sind Kopien, die das Steuergerät in Tabellen führt — im EEPROM-Bereich " +
            "und am Ende des ASW-Codes.\n\n" +
            $"Sollen sie auf 0x{repair.NewCrc:X8} gesetzt werden?\n\n" +
            "Ob das Steuergerät diese Kopien tatsächlich prüft, ist aus dem Abbild " +
            "nicht erkennbar.";

        var answer = MessageBox.Show(this, question, "Kopien des Prüfwerts mitziehen?",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes) return;

        foreach (long offset in repair.StaleCopies)
            _dump.PatchUInt32Be(offset, repair.NewCrc);

        _dump.Analyze(FixedOnly);
        Refresh();
        UpdateStatusLine($"{repair.StaleCopies.Count} Kopie(n) auf 0x{repair.NewCrc:X8} gesetzt");
    }

    private void OnExtractRegionClick(object sender, RoutedEventArgs e)
    {
        if (_dump is null || (sender as FrameworkElement)?.DataContext is not FlashRegion region) return;

        string stem = Path.GetFileNameWithoutExtension(_dump.FileName);
        var dialog = new SaveFileDialog
        {
            Title = $"{region.Label} herausschreiben",
            FileName = $"{stem}_0x{region.Start:X6}-0x{region.End:X6}.bin",
            Filter = "Rohdaten (*.bin)|*.bin|Alle Dateien (*.*)|*.*",
            InitialDirectory = _dump.Directory
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllBytes(dialog.FileName,
                               _dump.Raw.Slice((int)region.Start, (int)region.Length).ToArray());
            UpdateStatusLine($"{Path.GetFileName(dialog.FileName)} geschrieben ({region.SizeText})");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowProblem("Bereich lässt sich nicht schreiben", ex.Message);
        }
    }

    private void OnReplaceSectorClick(object sender, RoutedEventArgs e)
    {
        if (_dump is null || !Writable || SectorOf(sender) is not { } sector) return;

        var dialog = new OpenFileDialog
        {
            Title = $"{sector.Label} ersetzen durch…",
            FileName = sector.OutputName,
            Filter = "Sektordateien (*.*)|*.*",
            InitialDirectory = _dump.Directory,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            byte[] replacement = File.ReadAllBytes(dialog.FileName);
            var result = _dump.ReplaceSector(sector, replacement, repairCrc: true);
            _dump.Analyze(FixedOnly);

            var replaced = _dump.Sectors.FirstOrDefault(s => s.Start == sector.Start);
            if (replaced is not null)
            {
                string note = $"Ersetzt durch {Path.GetFileName(dialog.FileName)} " +
                              $"({replacement.Length:N0} B)";
                if (!result.HeaderFound)
                    note += " — kein gültiger Sektorkopf erkannt, Prüfsumme nicht neu berechnet";
                else
                {
                    note += ", Prüfsumme neu berechnet";
                    if (result.CpuOffsetChanged)
                        note += $". Achtung: CPU-Offset weicht ab " +
                                $"(war 0x{result.ExpectedCpuOffset:X6}, neu 0x{result.ActualCpuOffset:X6})";
                }
                note += ". Der Dump ist geändert und noch nicht gespeichert.";
                replaced.Note = note;
            }

            Refresh();
            UpdateStatusLine($"{sector.Label} ersetzt — Dump speichern nicht vergessen");
        }
        catch (InvalidOperationException ex)
        {
            ShowProblem("Sektor passt nicht", ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowProblem("Datei lässt sich nicht lesen", ex.Message);
        }
    }

    private void OnShowHeaderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        var card = FindAncestor<Border>(element, "CardRoot");
        if (card is null) return;
        if (FindDescendant<Border>(card, "HeaderDetails") is not { } details) return;

        details.Visibility = details.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    // ==================================================================
    // Übergreifende Aktionen
    // ==================================================================

    private void OnExtractAllClick(object sender, RoutedEventArgs e) => ExtractAll();

    private void ExtractAll()
    {
        if (_dump is null) return;

        int written = 0, mismatched = 0;
        foreach (var sector in _dump.Sectors.Where(s => s.Present))
        {
            try
            {
                string path = _dump.ExtractSector(sector);
                sector.Note = $"Geschrieben nach {path}";
                written++;
                if (!sector.CrcOk) mismatched++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                sector.Note = $"Nicht geschrieben: {ex.Message}";
            }
        }

        var parts = new List<string> { $"{written} Sektoren geschrieben nach {_dump.Directory}" };
        if (mismatched > 0) parts.Add($"{mismatched} mit abweichender Prüfsumme");
        UpdateStatusLine(string.Join("   ·   ", parts));
    }

    private void OnSaveDumpClick(object sender, RoutedEventArgs e) => SaveDump();

    /// <summary>Speichert den Dump. Gibt false zurück, wenn abgebrochen oder fehlgeschlagen.</summary>
    private bool SaveDump()
    {
        if (_dump is null || !Writable) return true;

        string stem = Path.GetFileNameWithoutExtension(_dump.FileName);
        string extension = Path.GetExtension(_dump.FileName);

        var dialog = new SaveFileDialog
        {
            Title = "Geänderten Dump speichern",
            FileName = stem + "_mod" + extension,
            Filter = $"Flash-Abbild (*{extension})|*{extension}|Alle Dateien (*.*)|*.*",
            InitialDirectory = _dump.Directory
        };
        if (dialog.ShowDialog(this) != true) return false;

        try
        {
            _dump.Save(dialog.FileName);
            Refresh();
            UpdateStatusLine($"Dump geschrieben nach {dialog.FileName}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowProblem("Dump lässt sich nicht schreiben", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Bietet bei ungesicherten Änderungen an zu speichern. Gibt false zurück,
    /// wenn der Vorgang abgebrochen werden soll (Abbrechen oder gescheitertes Speichern).
    /// </summary>
    private bool ConfirmDiscardChanges()
    {
        if (_dump is null || !_dump.IsModified) return true;

        var answer = MessageBox.Show(this,
            "Der Dump wurde geändert und ist noch nicht gespeichert.\n\n" +
            "Änderungen vor dem Fortfahren speichern?",
            "Ungesicherte Änderungen",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);

        return answer switch
        {
            MessageBoxResult.Yes => SaveDump(),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (_dump is null) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_dump.SourcePath}\"")
        {
            UseShellExecute = true
        });
    }

    private void OnCopyReportClick(object sender, RoutedEventArgs e)
    {
        if (_dump is null) return;

        try
        {
            Clipboard.SetText(DumpReport.Build(_dump));
            UpdateStatusLine("Bericht in der Zwischenablage");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            ShowProblem("Zwischenablage nicht verfügbar",
                        "Eine andere Anwendung belegt die Zwischenablage. Bitte erneut versuchen.");
        }
    }

    private void OnSaveReportClick(object sender, RoutedEventArgs e)
    {
        if (_dump is null) return;

        string stem = Path.GetFileNameWithoutExtension(_dump.FileName);
        var dialog = new SaveFileDialog
        {
            Title = "Bericht speichern",
            FileName = stem + "_bericht.txt",
            Filter = "Textdatei (*.txt)|*.txt|Alle Dateien (*.*)|*.*",
            InitialDirectory = _dump.Directory
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, DumpReport.Build(_dump));
            UpdateStatusLine($"Bericht geschrieben nach {dialog.FileName}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowProblem("Bericht lässt sich nicht schreiben", ex.Message);
        }
    }

    private void OnRepairAllClick(object sender, RoutedEventArgs e)
    {
        if (_dump is null || !Writable) return;

        var mismatched = _dump.Sectors.Where(s => s.Status == SectorStatus.CrcMismatch).ToList();
        if (mismatched.Count == 0) return;

        int withCopies = 0;
        foreach (var sector in mismatched)
            if (_dump.RepairCrc(sector).StaleCopies.Count > 0) withCopies++;

        _dump.Analyze(FixedOnly);
        Refresh();

        string message = $"{mismatched.Count} Prüfsumme(n) korrigiert";
        if (withCopies > 0) message += $" — {withCopies} mit veralteten Kopien im Abbild";
        UpdateStatusLine(message + "   ·   Dump speichern nicht vergessen");
    }

    private void OnScanToggled(object sender, RoutedEventArgs e)
    {
        if (_dump is null) return;
        _dump.Analyze(FixedOnly);
        Refresh();
    }

    // ==================================================================
    // Karte und Sektorliste verbinden
    // ==================================================================

    private void OnCardEnter(object sender, MouseEventArgs e) => Map.Highlighted = SectorOf(sender);

    private void OnCardLeave(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(Map.Highlighted, SectorOf(sender)))
            Map.Highlighted = null;
    }

    /// <summary>Holt die Karte eines auf der Adresskarte angeklickten Sektors in den Blick.</summary>
    private void ScrollToSector(SectorInfo sector)
    {
        Map.Highlighted = sector;
        if (SectorList.ItemContainerGenerator.ContainerFromItem(sector) is FrameworkElement container)
            container.BringIntoView();
    }

    // ==================================================================
    // Hilfsfunktionen
    // ==================================================================

    private void ShowProblem(string title, string detail) =>
        MessageBox.Show(this, detail, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    private static T? FindAncestor<T>(DependencyObject start, string name) where T : FrameworkElement
    {
        for (var node = start; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is T typed && typed.Name == name)
                return typed;
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed && typed.Name == name) return typed;
            if (FindDescendant<T>(child, name) is { } found) return found;
        }
        return null;
    }
}
