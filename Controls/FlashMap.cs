using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VolvoSplitter.Core;

namespace VolvoSplitter.Controls;

/// <summary>
/// Der Adressraum des Abbilds als senkrechte Karte: jeder gefundene Sektor
/// erscheint maßstäblich an seiner echten Position, der Rest ist gelöschtes
/// Flash. Zeigt auf einen Blick, wie wenig des Bausteins tatsächlich belegt ist
/// und wo die Sektoren liegen.
///
/// Sehr kleine Sektoren bekommen eine Mindesthöhe, sonst wären sie bei 8 MiB
/// Adressraum unsichtbar. Die Position bleibt in jedem Fall maßstäblich.
/// </summary>
public sealed class FlashMap : FrameworkElement
{
    private const double BandWidth = 40;
    private const double MinBandHeight = 4;
    private const double TopGutter = 18;
    private const double BottomGutter = 34;
    private const double LabelGap = 8;

    public static readonly DependencyProperty SectorsProperty =
        DependencyProperty.Register(nameof(Sectors), typeof(IReadOnlyList<SectorInfo>), typeof(FlashMap),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ImageSizeProperty =
        DependencyProperty.Register(nameof(ImageSize), typeof(long), typeof(FlashMap),
            new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RegionsProperty =
        DependencyProperty.Register(nameof(Regions), typeof(IReadOnlyList<FlashRegion>), typeof(FlashMap),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HighlightedProperty =
        DependencyProperty.Register(nameof(Highlighted), typeof(SectorInfo), typeof(FlashMap),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<SectorInfo>? Sectors
    {
        get => (IReadOnlyList<SectorInfo>?)GetValue(SectorsProperty);
        set => SetValue(SectorsProperty, value);
    }

    public long ImageSize
    {
        get => (long)GetValue(ImageSizeProperty);
        set => SetValue(ImageSizeProperty, value);
    }

    /// <summary>Belegte Bereiche ohne Sektorkopf — Code, opake Blöcke, NVM-Daten.</summary>
    public IReadOnlyList<FlashRegion>? Regions
    {
        get => (IReadOnlyList<FlashRegion>?)GetValue(RegionsProperty);
        set => SetValue(RegionsProperty, value);
    }

    public SectorInfo? Highlighted
    {
        get => (SectorInfo?)GetValue(HighlightedProperty);
        set => SetValue(HighlightedProperty, value);
    }

    /// <summary>Zeiger fährt über ein Sektorband (bzw. verlässt die Karte: null).</summary>
    public event EventHandler<SectorInfo?>? SectorHovered;

    /// <summary>Ein Sektorband wurde angeklickt.</summary>
    public event EventHandler<SectorInfo>? SectorClicked;

    /// <summary>Anklickbare Zonen je Sektor, in OnRender aus den Bandpositionen befüllt.</summary>
    private readonly List<(Rect Zone, SectorInfo Sector)> _zones = new();
    private SectorInfo? _hovered;

    private static Brush Res(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;

    protected override void OnRender(DrawingContext dc)
    {
        _zones.Clear();

        double height = ActualHeight - TopGutter - BottomGutter;
        if (ImageSize <= 0 || height <= 0 || ActualWidth < BandWidth) return;

        // Transparente Vollfläche, damit die Maus-Interaktion überall anspricht —
        // sonst reagiert nur der bemalte Bereich.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        double bandLeft = ActualWidth - BandWidth - 6;
        double top = TopGutter;

        var erased = Res("ErasedBrush", Brushes.DimGray);
        var line = Res("LineBrush", Brushes.Gray);
        var muted = Res("MutedBrush", Brushes.Gray);
        var text = Res("TextBrush", Brushes.White);
        var signal = Res("SignalBrush", Brushes.SteelBlue);
        var warn = Res("WarnBrush", Brushes.Orange);

        // Der Baustein als Objekt: gelöschtes Flash mit klarer Kante.
        var chip = new Rect(bandLeft, top, BandWidth, height);
        dc.DrawRectangle(erased, null, chip);

        // 1-MiB-Raster — Gitter, nicht Inhalt: nur eine Spur heller als der Grund.
        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(0x21, 0x29, 0x33)), 1);
        gridPen.Freeze();
        for (long address = 0x100000; address < ImageSize; address += 0x100000)
        {
            double y = Snap(top + height * address / (double)ImageSize);
            dc.DrawLine(gridPen, new Point(bandLeft, y), new Point(bandLeft + BandWidth, y));
        }

        var edgePen = new Pen(line, 1);
        edgePen.Freeze();
        dc.DrawRectangle(null, edgePen, chip);

        long occupied = 0;

        // Bereiche ohne Sektorkopf zuerst, in gedeckten Tönen: sie sind der
        // Untergrund, vor dem die benannten Sektoren stehen. Die Art bleibt
        // dabei unterscheidbar — Code, NVM-Daten und opake Blöcke sind
        // grundverschiedene Dinge und dürfen nicht gleich aussehen.
        foreach (var region in Regions ?? [])
        {
            occupied += region.Length;
            double ry = Snap(top + height * region.Start / (double)ImageSize);
            double rh = Math.Max(MinBandHeight, Math.Round(height * region.Length / (double)ImageSize));
            dc.DrawRectangle(RegionBrush(region.Kind), null, new Rect(bandLeft, ry, BandWidth, rh));
        }

        double lastLabelBottom = double.NegativeInfinity;

        foreach (var sector in (Sectors ?? []).OrderBy(s => s.Start))
        {
            if (!sector.Present) continue;
            occupied += sector.Length;

            double y = Snap(top + height * sector.Start / (double)ImageSize);
            double h = Math.Max(MinBandHeight, Math.Round(height * sector.Length / (double)ImageSize));
            var band = new Rect(bandLeft, y, BandWidth, h);

            // Anklickbare Zone über die volle Breite, damit auch dünne Bänder treffbar sind.
            _zones.Add((new Rect(0, y, ActualWidth, h), sector));

            bool active = ReferenceEquals(sector, Highlighted);
            var fill = sector.CrcOk ? signal : warn;

            if (!active && fill is SolidColorBrush solid)
            {
                var quiet = new SolidColorBrush(solid.Color) { Opacity = 0.8 };
                quiet.Freeze();
                fill = quiet;
            }

            dc.DrawRectangle(fill, null, band);

            // Adresse links neben dem Band, solange sie nicht mit der vorigen
            // kollidiert. Beim hervorgehobenen Sektor immer.
            var label = Label($"0x{sector.Start:X6}", active ? text : muted, 10);
            double labelTop = band.Top + band.Height / 2 - label.Height / 2;

            if (active || labelTop > lastLabelBottom + 2)
            {
                dc.DrawText(label, new Point(bandLeft - LabelGap - label.Width, labelTop));
                lastLabelBottom = labelTop + label.Height;
            }

            if (active)
            {
                // Ausleger zur zugehörigen Karte rechts.
                var marker = new Pen(fill, 1.5);
                marker.Freeze();
                double mid = band.Top + band.Height / 2;
                dc.DrawLine(marker, new Point(bandLeft + BandWidth, mid),
                                    new Point(ActualWidth, mid));
            }
        }

        // Grenzen des Adressraums
        var start = Label("0x000000", muted, 9);
        dc.DrawText(start, new Point(bandLeft + BandWidth - start.Width, top - start.Height - 4));

        var stop = Label($"0x{ImageSize:X6}", muted, 9);
        dc.DrawText(stop, new Point(bandLeft + BandWidth - stop.Width, top + height + 4));

        // Beschriftung: wie viel des Bausteins überhaupt belegt ist.
        if (occupied > 0)
        {
            double percent = 100.0 * occupied / ImageSize;
            var caption = Label($"{percent:0.0} % belegt", muted, 9);
            dc.DrawText(caption, new Point(bandLeft + BandWidth - caption.Width,
                                           top + height + 4 + stop.Height + 3));
        }
    }

    private static double Snap(double value) => Math.Round(value) + 0.5;

    /// <summary>
    /// Gedeckte Töne je Art des Bereichs — hell genug zum Unterscheiden, dunkel
    /// genug, damit die benannten Sektoren davor stehen bleiben.
    /// </summary>
    private static Brush RegionBrush(RegionKind kind) => kind switch
    {
        RegionKind.Code => Frozen(0x33, 0x44, 0x58),
        RegionKind.NvmData => Frozen(0x4C, 0x42, 0x33),
        RegionKind.Opaque => Frozen(0x3A, 0x45, 0x52),
        _ => Frozen(0x30, 0x37, 0x3F)
    };

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    // ------------------------------------------------------------------
    // Interaktion: Band überfahren oder anklicken
    // ------------------------------------------------------------------

    private SectorInfo? ZoneAt(Point point)
    {
        foreach (var (zone, sector) in _zones)
            if (zone.Contains(point)) return sector;
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var sector = ZoneAt(e.GetPosition(this));
        Cursor = sector is null ? Cursors.Arrow : Cursors.Hand;

        if (!ReferenceEquals(sector, _hovered))
        {
            _hovered = sector;
            SectorHovered?.Invoke(this, sector);
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hovered is not null)
        {
            _hovered = null;
            SectorHovered?.Invoke(this, null);
        }
        Cursor = Cursors.Arrow;
        base.OnMouseLeave(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (ZoneAt(e.GetPosition(this)) is { } sector)
            SectorClicked?.Invoke(this, sector);
        base.OnMouseLeftButtonUp(e);
    }

    private FormattedText Label(string value, Brush brush, double size) =>
        new(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Cascadia Mono, Consolas"),
                         FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
