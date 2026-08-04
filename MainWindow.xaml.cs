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
    /// <summary>
    /// Das Abbild, das bearbeitet und gespeichert wird. Alles Verändernde meint
    /// dieses — Prüfsumme stellen, Sektor ersetzen, Block übernehmen, speichern,
    /// schließen. Hieß bis v1.3.0 <c>_dump</c>; mit zwei Abbildern im Fenster
    /// benennt das nichts mehr.
    /// </summary>
    private FlashDump? _target;

    /// <summary>
    /// Zweites Abbild, aus dem einzelne Blöcke übernommen werden. Wird
    /// ausschließlich gelesen: kein Pfad in dieser Datei ruft darauf
    /// <c>RepairCrc</c>, <c>PatchUInt32Be</c>, <c>ReplaceSector</c>,
    /// <c>CopyBlockFrom</c> oder <c>Save</c>. Es gibt für die Quelle keinen
    /// Speichern-Knopf, weil es nichts zu speichern gibt.
    /// </summary>
    private FlashDump? _source;

    public MainWindow()
    {
        InitializeComponent();
        WindowTheme.ApplyDark(this);

        Map.SectorHovered += (_, sector) => Map.Highlighted = sector;
        Map.SectorClicked += (_, sector) => ScrollToSector(sector);

        Loaded += (_, _) =>
        {
            if (App.StartupFile is not { } path || !File.Exists(path)) return;

            LoadFile(path);

            if (_target is not null && App.StartupSource is { } second && File.Exists(second))
                LoadSource(second);
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
            _target = FlashDump.Load(path, FixedOnly);
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
        if (_target is null) return;

        FileNameText.Text = _target.FileName;
        FileMetaText.Text = $"{_target.Size:N0} B  ·  {Hex.Addr(_target.Size)}  ·  " +
                            $"{_target.Profile.FamilyName}  ·  {_target.Profile.MicroName}  ·  " +
                            _target.Profile.Manufacturer;

        ModifiedBadge.Visibility = _target.IsModified ? Visibility.Visible : Visibility.Collapsed;
        SaveDumpButton.Visibility = _target.IsModified && Saveable
            ? Visibility.Visible : Visibility.Collapsed;

        UpdateDetectionBadges();

        UpdateIdentity();

        SectorList.ItemsSource = null;
        SectorList.ItemsSource = _target.Sectors;

        RegionList.ItemsSource = null;
        RegionList.ItemsSource = _target.Regions;
        RegionSection.Visibility = _target.Regions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        PartitionList.ItemsSource = null;
        PartitionList.ItemsSource = _target.Partitions;
        PartitionSection.Visibility = _target.Partitions.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;

        // Die Herkunft der Blockkarte steht nicht mehr fest im Text, sondern
        // kommt aus dem Layout selbst.
        LayoutSourceText.Text = _target.Layout is { } layout
            ? layout.SourceNote + (layout.Complete ? "" : " — Zuordnung nicht vollständig")
            : "";

        Map.ImageSize = _target.Size;
        Map.Sectors = _target.Sectors;
        Map.Regions = _target.Regions;
        Map.Partitions = _target.Layout?.Partitions;
        Map.Highlighted = null;

        // Zuletzt, weil die Vermerke der Quelle am Ziel hängen: ändert sich das
        // Ziel, ändern sich die Gegenstücke.
        RefreshSource();

        UpdateStatusLine();
        AnimateCards();
    }

    /// <summary>Das erkannte Profil sieht Zurückschreiben vor.</summary>
    private bool Writable => _target?.Profile.SupportsWriteBack == true;

    /// <summary>Das Ziel darf einen Block aus einem anderen Abbild übernehmen.</summary>
    private bool CanReceiveBlocks => _target?.Profile.SupportsBlockTransfer == true;

    /// <summary>
    /// Es gibt für dieses Abbild überhaupt einen verändernden Vorgang — also
    /// auch etwas zu speichern. Weiter gefasst als <see cref="Writable"/>: ein
    /// übernommener Block muss sich sichern lassen, auch dort, wo das Werkzeug
    /// keine Prüfsumme stellt.
    /// </summary>
    private bool Saveable => _target?.Profile.SupportsSaving == true;

    /// <summary>
    /// Zwei Abzeichen neben dem Dateinamen: eine knappe Erkennung wird als
    /// „nicht eindeutig" ausgewiesen statt stillschweigend entschieden, und ein
    /// Profil ohne Zurückschreiben sagt das offen.
    /// </summary>
    private void UpdateDetectionBadges()
    {
        if (_target is null) return;

        var detection = _target.Detection;
        bool unsure = detection.Ambiguous || detection.DeviceAmbiguous;

        AmbiguousBadge.Visibility = unsure ? Visibility.Visible : Visibility.Collapsed;
        AmbiguousText.Text = detection.DeviceAmbiguous && detection.DeviceCandidates.Count > 0
            ? "Baustein offen: " + string.Join(" oder ", detection.DeviceCandidates)
            : "Zuordnung nicht eindeutig";
        AmbiguousBadge.ToolTip = string.Join("\n", detection.Evidence);

        ReadOnlyBadge.Visibility = Writable ? Visibility.Collapsed : Visibility.Visible;
        ReadOnlyBadge.ToolTip = Writable
            ? null
            : $"Für {_target.Profile.FamilyName} werden Prüfsummen gerechnet und gemeldet, " +
              "aber nicht gestellt. Korrigieren und Ersetzen sind deshalb abgeschaltet. " +
              "Ein Block lässt sich aus einem zweiten Abbild 1:1 übernehmen — dabei werden " +
              "Bytes übernommen, nicht Werte gestellt.";
    }

    private void UpdateIdentity()
    {
        if (_target is null) return;

        var vehicle = _target.Vehicle;
        var report = _target.Report;
        var identity = _target.Identity;

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
        if (_target?.Chain.Variant is { } variant)
            parts.Add($"Variante {variant}");
        if (_target?.Chain.Cvn is { } cvn)
            parts.Add($"CVN {cvn.ValueText}");

        EcuMetaText.Text = string.Join("   ·   ", parts);
        EcuMetaText.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateStatusLine(string? message = null)
    {
        if (_target is null) return;

        if (message is not null)
        {
            StatusLine.Text = message;
            return;
        }

        int found = _target.Sectors.Count(s => s.Present);
        int mismatched = _target.Sectors.Count(s => s.Status == SectorStatus.CrcMismatch);
        int missing = _target.Sectors.Count(s => !s.Present);

        var parts = new List<string> { $"{found} Sektoren gefunden" };
        if (mismatched > 0) parts.Add($"{mismatched} × Prüfsumme weicht ab");
        if (missing > 0) parts.Add($"{missing} nicht gefunden");
        if (_target.Regions.Count > 0) parts.Add($"{_target.Regions.Count} Bereiche ohne Kopf");

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
        if (_target is not null) dialog.InitialDirectory = _target.Directory;
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

    /// <summary>
    /// Eine Datei wird das Ziel — wie bisher. Zwei Dateien auf einmal sind die
    /// eindeutige Geste für „beide öffnen": die erste als Ziel, die zweite als
    /// Quelle. Eine Rückfrage bei jeder Einzelablage wäre der Preis dafür, eine
    /// eingespielte Geste mehrdeutig zu machen.
    /// </summary>
    private void OnDrop(object sender, DragEventArgs e)
    {
        ResetDropZone();
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;

        LoadFile(files[0]);

        if (files.Length > 1 && _target is not null)
            LoadSource(files[1]);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = e.KeyboardDevice.Modifiers;
        bool shift = modifiers.HasFlag(ModifierKeys.Shift);

        // Alt und Windows-Taste bleiben außen vor — die gehören dem System.
        // Umschalt wird dagegen ausgewertet, und deshalb muss jeder bestehende
        // Zweig es ausdrücklich ausschließen: sonst löste Strg+Umschalt+S
        // plötzlich Speichern aus.
        if (!modifiers.HasFlag(ModifierKeys.Control) ||
            modifiers.HasFlag(ModifierKeys.Alt) ||
            modifiers.HasFlag(ModifierKeys.Windows)) return;

        switch (e.Key)
        {
            case Key.O when !shift:
                PickFile();
                e.Handled = true;
                break;
            case Key.O when shift && _target is not null:
                PickSourceFile();
                e.Handled = true;
                break;
            case Key.W when shift && _source is not null:
                CloseSource();
                e.Handled = true;
                break;
            case Key.S when !shift && _target?.IsModified == true && Saveable:
                SaveDump();
                e.Handled = true;
                break;
            case Key.E when !shift && _target is not null:
                ExtractAll();
                e.Handled = true;
                break;
        }
    }

    // ==================================================================
    // Sektoraktionen
    // ==================================================================

    /// <summary>
    /// Der Sektor hinter einem Bedienelement. Die Quellleiste bindet
    /// ausdrücklich an <see cref="SourceBlock"/> und nicht an
    /// <see cref="SectorInfo"/> — deshalb liefert das hier für alles in der
    /// Leiste null, und jeder Zielpfad steigt an seiner ersten Zeile aus, ohne
    /// dass sich jemand daran erinnern muss.
    /// </summary>
    private static SectorInfo? SectorOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as SectorInfo;

    private void OnExtractSectorClick(object sender, RoutedEventArgs e)
    {
        if (_target is null || SectorOf(sender) is not { } sector) return;

        try
        {
            string path = _target.ExtractSector(sector);
            // Drei Zustände, drei Sätze. Über die Verneinung von CrcOk zu gehen
            // hieße, den nie gestellten Block einer Abweichung zu bezichtigen.
            sector.Note = sector.Status switch
            {
                SectorStatus.CrcMismatch =>
                    $"Geschrieben nach {path} — Prüfsumme weicht ab, Inhalt unverändert übernommen",
                SectorStatus.ChecksumNotStamped =>
                    $"Geschrieben nach {path} — für diesen Block wurde nie eine Prüfsumme gestellt",
                _ => $"Geschrieben nach {path}"
            };
            UpdateStatusLine($"{sector.OutputName} geschrieben");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowProblem("Sektor lässt sich nicht schreiben", ex.Message);
        }
    }

    private void OnExportSRecordClick(object sender, RoutedEventArgs e)
    {
        if (_target is null || SectorOf(sender) is not { } sector) return;

        var dialog = new SaveFileDialog
        {
            Title = "Als S-Record speichern",
            FileName = sector.OutputName + ".s3",
            Filter = "Motorola S-Record (*.s3)|*.s3|Alle Dateien (*.*)|*.*",
            InitialDirectory = _target.Directory
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            _target.ExportSRecord(sector, dialog.FileName);
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
        if (_target is null || !Writable || SectorOf(sender) is not { } sector) return;

        var repair = _target.RepairCrc(sector);
        _target.Analyze(FixedOnly);

        var repaired = _target.Sectors.FirstOrDefault(s => s.Start == sector.Start);
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
        if (_target is null) return;

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
            _target.PatchUInt32Be(offset, repair.NewCrc);

        _target.Analyze(FixedOnly);
        Refresh();
        UpdateStatusLine($"{repair.StaleCopies.Count} Kopie(n) auf 0x{repair.NewCrc:X8} gesetzt");
    }

    private void OnExtractRegionClick(object sender, RoutedEventArgs e)
    {
        if (_target is null || (sender as FrameworkElement)?.DataContext is not FlashRegion region) return;

        string stem = Path.GetFileNameWithoutExtension(_target.FileName);
        var dialog = new SaveFileDialog
        {
            Title = $"{region.Label} herausschreiben",
            FileName = $"{stem}_0x{region.Start:X6}-0x{region.End:X6}.bin",
            Filter = "Rohdaten (*.bin)|*.bin|Alle Dateien (*.*)|*.*",
            InitialDirectory = _target.Directory
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllBytes(dialog.FileName,
                               _target.Raw.Slice((int)region.Start, (int)region.Length).ToArray());
            UpdateStatusLine($"{Path.GetFileName(dialog.FileName)} geschrieben ({region.SizeText})");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowProblem("Bereich lässt sich nicht schreiben", ex.Message);
        }
    }

    private void OnReplaceSectorClick(object sender, RoutedEventArgs e)
    {
        if (_target is null || !Writable || SectorOf(sender) is not { } sector) return;

        var dialog = new OpenFileDialog
        {
            Title = $"{sector.Label} ersetzen durch…",
            FileName = sector.OutputName,
            Filter = "Sektordateien (*.*)|*.*",
            InitialDirectory = _target.Directory,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            byte[] replacement = File.ReadAllBytes(dialog.FileName);
            var result = _target.ReplaceSector(sector, replacement, repairCrc: true);
            _target.Analyze(FixedOnly);

            var replaced = _target.Sectors.FirstOrDefault(s => s.Start == sector.Start);
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
    // Quelle: zweites Abbild, aus dem Blöcke übernommen werden
    // ==================================================================

    private void OnOpenSourceClick(object sender, RoutedEventArgs e) => PickSourceFile();

    private void PickSourceFile()
    {
        if (_target is null) return;

        var dialog = new OpenFileDialog
        {
            Title = "Quelle wählen — wird nur gelesen",
            Filter = "Flash-Abbilder (*.mpc;*.bin;*.ori)|*.mpc;*.bin;*.ori|Alle Dateien (*.*)|*.*",
            InitialDirectory = _target.Directory,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true) LoadSource(dialog.FileName);
    }

    /// <summary>
    /// Lädt das zweite Abbild. Anders als <see cref="LoadFile"/> ohne Rückfrage
    /// nach ungesicherten Änderungen: hier geht nichts verloren, das Ziel bleibt
    /// unangetastet.
    /// </summary>
    private void LoadSource(string path)
    {
        try
        {
            _source = FlashDump.Load(path, FixedOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or OutOfMemoryException)
        {
            ShowProblem("Quelle lässt sich nicht laden", ex.Message);
            return;
        }

        bool wasClosed = SourcePane.Visibility != Visibility.Visible;
        SourcePane.Visibility = Visibility.Visible;
        if (wasClosed) GrowForSource();

        Refresh();
        UpdateStatusLine($"Quelle {_source.FileName} geöffnet — wird nur gelesen");
    }

    private void OnCloseSourceClick(object sender, RoutedEventArgs e) => CloseSource();

    /// <summary>
    /// Schließt die Quelle. Keine Rückfrage: an ihr wurde nichts geändert, weil
    /// an ihr nichts geändert werden kann. Das Fenster behält seine Breite — sie
    /// zurückzunehmen hieße, dem Nutzer eine Größe wegzunehmen, die inzwischen
    /// seine sein kann.
    /// </summary>
    private void CloseSource()
    {
        _source = null;
        SourcePane.Visibility = Visibility.Collapsed;
        SourceList.ItemsSource = null;
        TargetRoleText.Visibility = Visibility.Collapsed;
        UpdateStatusLine("Quelle geschlossen");
    }

    /// <summary>
    /// Tauscht die Rollen. Zuerst die Rückfrage nach ungesicherten Änderungen:
    /// sonst wanderte das geänderte Ziel in eine Fläche, aus der es sich nicht
    /// speichern lässt.
    /// </summary>
    private void OnSwapRolesClick(object sender, RoutedEventArgs e)
    {
        if (_target is null || _source is null) return;
        if (!ConfirmDiscardChanges()) return;

        (_target, _source) = (_source, _target);

        Refresh();
        UpdateStatusLine($"Rollen getauscht — Ziel ist jetzt {_target.FileName}");
    }

    /// <summary>
    /// Macht das Fenster einmalig um die Breite der Leiste breiter, soweit der
    /// Bildschirm es hergibt. Die Mindestbreite bleibt, wie sie ist: sie gilt
    /// auch ohne Quelle, und sie anzuheben bestrafte den Normalfall.
    /// </summary>
    private void GrowForSource()
    {
        if (WindowState != WindowState.Normal) return;

        double room = SystemParameters.WorkArea.Right - Left;
        Width = Math.Min(Width + SourcePane.Width, Math.Max(Width, room));
    }

    /// <summary>
    /// Schreibt Kopf und Liste der Quellleiste neu. Läuft aus
    /// <see cref="Refresh"/> mit, weil jeder Vermerk der Quelle eine Aussage
    /// über das Ziel ist — ändert sich das Ziel, ändern sich die Vermerke.
    /// </summary>
    private void RefreshSource()
    {
        if (_source is null || _target is null)
        {
            SourcePane.Visibility = Visibility.Collapsed;
            TargetRoleText.Visibility = Visibility.Collapsed;
            return;
        }

        TargetRoleText.Visibility = Visibility.Visible;

        SourceFileNameText.Text = _source.FileName;
        SourceMetaText.Text = $"{_source.Size:N0} B  ·  {_source.Profile.FamilyName}  ·  " +
                              $"{_source.Sectors.Count(s => s.Present)} Blöcke";

        string? notice = SourceNotice();
        SourceNoticeText.Text = notice ?? "";
        SourceNoticeText.Visibility = notice is null ? Visibility.Collapsed : Visibility.Visible;

        // Der Plan kommt aus derselben Prüfung, die auch der Vorgang benutzt.
        // Deshalb kann die Karte nichts anderes behaupten, als der Knopf tut.
        var blocks = _source.Sectors
                            .Where(s => s.Present)
                            .Select(s => new SourceBlock(s, BlockTransfer.Prepare(_source, s, _target)))
                            .ToList();

        SourceList.ItemsSource = null;
        SourceList.ItemsSource = blocks;

        SourceEmptyText.Visibility = blocks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Ein Grund, der für das ganze Abbild gilt und deshalb nicht an jeder Karte
    /// wiederholt werden muss. Null, wenn nichts im Weg steht.
    /// </summary>
    private string? SourceNotice()
    {
        if (_source is null || _target is null) return null;

        if (string.Equals(_source.SourcePath, _target.SourcePath,
                          StringComparison.OrdinalIgnoreCase))
            return "Dieselbe Datei wie das Ziel — die Quelle zeigt den Stand auf der Platte, " +
                   "das Ziel die geänderte Arbeitskopie.";

        if (!CanReceiveBlocks)
            return $"Für {_target.Profile.FamilyName} ist kein Blockübertrag vorgesehen: ohne " +
                   "erkannte Blockstruktur gibt es im Ziel keinen Block, der ein Gegenstück wäre.";

        if (_source.Profile.Container != _target.Profile.Container)
            return $"Quelle ({_source.Profile.FamilyName}) und Ziel ({_target.Profile.FamilyName}) " +
                   "haben verschiedene Containerformate — ein Block der einen Art hat in der " +
                   "anderen kein Gegenstück.";

        return null;
    }

    private static SourceBlock? SourceBlockOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as SourceBlock;

    /// <summary>
    /// Übernimmt einen Block der Quelle ins Ziel. Der Aufbau folgt
    /// <see cref="OnReplaceSectorClick"/>; es fehlt der IO-Fänger, weil keine
    /// Datei angefasst wird — die Bytes stehen schon im Speicher.
    /// </summary>
    private void OnSendToTargetClick(object sender, RoutedEventArgs e)
    {
        if (_target is null || _source is null || SourceBlockOf(sender) is not { } item) return;

        // Noch einmal prüfen: zwischen dem Auffrischen der Karte und diesem
        // Klick kann sich das Ziel geändert haben.
        var plan = BlockTransfer.Prepare(_source, item.Sector, _target);

        if (!plan.Possible)
        {
            ShowProblem("Kein Gegenstück im Ziel", plan.RejectionText);
            Refresh();
            return;
        }

        if (plan.TargetBlock is not { } hit) return;
        if (!OfferBlockCopy(plan, hit)) return;

        try
        {
            var result = _target.CopyBlockFrom(plan, FixedOnly);

            // Analyze baut neue Objekte — der Vermerk gehört an den
            // wiedergefundenen Block, nicht an den alten Verweis.
            if (_target.Sectors.FirstOrDefault(s => s.Start == hit.Start) is { } copied)
                copied.Note = CopyNote(item.Sector, result);

            item.Sector.Note = $"→ nach {Hex.Addr(hit.CpuOffset)} ins Ziel übernommen";

            Refresh();
            UpdateStatusLine($"{hit.Label} aus {_source.FileName} übernommen — " +
                             "Dump speichern nicht vergessen");
        }
        catch (InvalidOperationException ex)
        {
            ShowProblem("Block passt nicht", ex.Message);
        }
    }

    /// <summary>
    /// Fragt vor dem Überschreiben. Ausgeschrieben wird, was verschwindet, was
    /// an seine Stelle tritt und was das Werkzeug dabei ausdrücklich
    /// <em>nicht</em> tut — gerade der letzte Teil ist der, den man hinterher
    /// braucht. Vorgabe ist Nein.
    /// </summary>
    private bool OfferBlockCopy(BlockTransferPlan plan, SectorInfo hit)
    {
        if (_source is null || _target is null) return false;

        var origin = plan.SourceBlock;

        var text = new System.Text.StringBuilder()
            .AppendLine($"Übernehmen: {origin.LabelText}  {origin.PartNumber}")
            .AppendLine($"aus {_source.FileName}")
            .AppendLine()
            .AppendLine($"Überschrieben wird in {_target.FileName}:")
            .AppendLine($"   {hit.LabelText}  {hit.PartNumber}")
            .AppendLine($"   Datei {hit.AddressRange}   ·   CPU {hit.CpuAddressRange}")
            .AppendLine($"   {hit.SizeText}")
            .AppendLine()
            .AppendLine($"Geschrieben werden {origin.Length:N0} B an dieselbe CPU-Adresse " +
                        $"{Hex.Addr(hit.CpuOffset)}.");

        if (plan.TailErased > 0)
            text.AppendLine($"Die restlichen {plan.TailErased:N0} B bis {Hex.Addr(hit.End)} " +
                            "werden auf 0xFF gesetzt.");

        foreach (var finding in plan.Warnings)
            text.AppendLine("   · " + finding.Text);

        text.AppendLine()
            .AppendLine("Nicht nachgezogen wird:")
            .AppendLine("   · die Prüfsumme — die Bytes werden unverändert übernommen, " +
                        "es wird nichts gestellt");

        if (hit.HasChecksumCopies)
            text.AppendLine($"   · der Prüfwert 0x{hit.CrcStored:X8} des ersetzten Blocks, der " +
                            "auch bei " +
                            string.Join(", ", hit.ChecksumCopies.Select(Hex.Addr)) + " steht");

        if (_target.Profile.Container == ContainerKind.BoschBlockChain)
            text.AppendLine("   · die Blockkette (nextSector), die Prüfsummenstrukturen anderer " +
                            "Blöcke, die CVN und die Variantenkennung im Dataset-Block");

        text.AppendLine()
            .AppendLine("Ob das Steuergerät das Ergebnis annimmt, ist aus dem Abbild nicht " +
                        "erkennbar. Geändert wird die Arbeitskopie im Speicher; die " +
                        "Originaldatei bleibt unangetastet.")
            .AppendLine()
            .Append("Übernehmen?");

        return MessageBox.Show(this, text.ToString(), "Block aus der Quelle übernehmen?",
                               MessageBoxButton.YesNo, MessageBoxImage.Question,
                               MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    /// <summary>Was am übernommenen Block im Ziel stehen bleibt.</summary>
    private string CopyNote(SectorInfo origin, BlockTransferResult result)
    {
        var note = new System.Text.StringBuilder(
            $"Übernommen aus {_source?.FileName} · {origin.LabelText} {origin.PartNumber} " +
            $"({result.BytesWritten:N0} B, CPU {Hex.Addr(origin.CpuOffset)})");

        if (!result.HeaderStillValid)
            note.Append(" — Achtung: der übernommene Block wird an dieser Stelle nicht mehr " +
                        "als Block gelesen");

        note.Append(result.StatusAfter switch
        {
            SectorStatus.Verified => ". Prüfsumme geht auf",
            SectorStatus.CrcMismatch => ". Prüfsumme weicht ab — sie wurde nicht gestellt",
            SectorStatus.ChecksumNotStamped => ". Für diesen Block wurde nie eine Prüfsumme gestellt",
            _ => ""
        });

        if (result.Broken.Any())
            note.Append($". {result.Broken.Count()} Prüfsumme(n) anderswo im Abbild gehen " +
                        "seitdem nicht mehr auf");

        if (result.CvnChanged)
            note.Append($". Die CVN ist jetzt 0x{result.CvnAfter:X8} " +
                        $"(vorher 0x{result.CvnBefore:X8}) — sie wird gelesen, nicht gestellt");

        note.Append(". Der Dump ist geändert und noch nicht gespeichert.");
        return note.ToString();
    }

    private void OnDragOverSource(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// Ablage auf der Leiste selbst: wird Quelle, ohne Rückfrage. Hier kann
    /// nichts verlorengehen, und der Ablageort sagt die Absicht.
    /// </summary>
    private void OnDropSource(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            LoadSource(files[0]);
    }

    // ==================================================================
    // Übergreifende Aktionen
    // ==================================================================

    private void OnExtractAllClick(object sender, RoutedEventArgs e) => ExtractAll();

    private void ExtractAll()
    {
        if (_target is null) return;

        int written = 0, mismatched = 0;
        foreach (var sector in _target.Sectors.Where(s => s.Present))
        {
            try
            {
                string path = _target.ExtractSector(sector);
                sector.Note = $"Geschrieben nach {path}";
                written++;

                // Gezählt wird die Abweichung, nicht „alles außer bestätigt“:
                // die Zeile darunter schreibt „mit abweichender Prüfsumme“, und
                // das gilt für einen nie gestellten Block nicht.
                if (sector.Status == SectorStatus.CrcMismatch) mismatched++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                sector.Note = $"Nicht geschrieben: {ex.Message}";
            }
        }

        var parts = new List<string> { $"{written} Sektoren geschrieben nach {_target.Directory}" };
        if (mismatched > 0) parts.Add($"{mismatched} mit abweichender Prüfsumme");
        UpdateStatusLine(string.Join("   ·   ", parts));
    }

    private void OnSaveDumpClick(object sender, RoutedEventArgs e) => SaveDump();

    /// <summary>Speichert den Dump. Gibt false zurück, wenn abgebrochen oder fehlgeschlagen.</summary>
    private bool SaveDump()
    {
        if (_target is null || !Saveable) return true;

        string stem = Path.GetFileNameWithoutExtension(_target.FileName);
        string extension = Path.GetExtension(_target.FileName);

        var dialog = new SaveFileDialog
        {
            Title = "Geänderten Dump speichern",
            FileName = stem + "_mod" + extension,
            Filter = $"Flash-Abbild (*{extension})|*{extension}|Alle Dateien (*.*)|*.*",
            InitialDirectory = _target.Directory
        };
        if (dialog.ShowDialog(this) != true) return false;

        try
        {
            _target.Save(dialog.FileName);
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
        if (_target is null || !_target.IsModified) return true;

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
        if (_target is null) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_target.SourcePath}\"")
        {
            UseShellExecute = true
        });
    }

    private void OnCopyReportClick(object sender, RoutedEventArgs e)
    {
        if (_target is null) return;

        try
        {
            Clipboard.SetText(DumpReport.Build(_target));
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
        if (_target is null) return;

        string stem = Path.GetFileNameWithoutExtension(_target.FileName);
        var dialog = new SaveFileDialog
        {
            Title = "Bericht speichern",
            FileName = stem + "_bericht.txt",
            DefaultExt = "txt",
            Filter = "Textdatei (*.txt)|*.txt|" +
                     "Markdown (*.md)|*.md|" +
                     "HTML-Seite (*.html)|*.html|" +
                     "Alle Dateien (*.*)|*.*",
            InitialDirectory = _target.Directory
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            // Die Endung entscheidet, nicht der gewählte Filter — dann stimmt es
            // auch, wenn jemand den Namen samt Endung von Hand tippt.
            var format = DumpReport.FormatFor(dialog.FileName);

            File.WriteAllText(dialog.FileName, DumpReport.Build(_target, format));
            UpdateStatusLine($"Bericht geschrieben nach {dialog.FileName}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowProblem("Bericht lässt sich nicht schreiben", ex.Message);
        }
    }

    private void OnRepairAllClick(object sender, RoutedEventArgs e)
    {
        if (_target is null || !Writable) return;

        var mismatched = _target.Sectors.Where(s => s.Status == SectorStatus.CrcMismatch).ToList();
        if (mismatched.Count == 0) return;

        int withCopies = 0;
        foreach (var sector in mismatched)
            if (_target.RepairCrc(sector).StaleCopies.Count > 0) withCopies++;

        _target.Analyze(FixedOnly);
        Refresh();

        string message = $"{mismatched.Count} Prüfsumme(n) korrigiert";
        if (withCopies > 0) message += $" — {withCopies} mit veralteten Kopien im Abbild";
        UpdateStatusLine(message + "   ·   Dump speichern nicht vergessen");
    }

    private void OnScanToggled(object sender, RoutedEventArgs e)
    {
        if (_target is null) return;

        _target.Analyze(FixedOnly);

        // Beide Abbilder nach derselben Regel lesen — sonst würde ein Block der
        // einen Lesart gegen ein Ziel der anderen gehalten.
        _source?.Analyze(FixedOnly);

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
