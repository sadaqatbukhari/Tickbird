using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Tickbird.Core.Data;
using Tickbird.Core.Mapping;
using Tickbird.Core.Query;
using Tickbird.Desktop.Services;

namespace Tickbird.Desktop.Views.Requests;

/// <summary>
/// Map preferences shared by every result tab of one query tab (like <see cref="ChartSettings"/>).
/// <see cref="Version"/> changes on every edit.
/// </summary>
public sealed class MapSettings
{
    private string? latitudeColumn;
    private string? longitudeColumn;
    private string? colorColumn;
    private MapColorMode? colorMode;
    private int rangeCount = 5;
    private string? splitBy;
    private bool combineResults = true;
    private double lineWidth = 3;
    private string background = MapBackground.Hybrid.Name;
    private bool showEndpoints = true;
    private string otherColor = "#9E9E9E";
    private readonly Dictionary<string, string> colors = new(StringComparer.Ordinal);

    public int Version { get; private set; }

    /// <summary>Bound latitude column; null picks a column named like "lat".</summary>
    public string? LatitudeColumn { get => latitudeColumn; set => Set(ref latitudeColumn, value); }

    /// <summary>Bound longitude column; null picks a column named like "lon".</summary>
    public string? LongitudeColumn { get => longitudeColumn; set => Set(ref longitudeColumn, value); }

    /// <summary>Column whose values color the lines; null for one color per track.</summary>
    public string? ColorColumn { get => colorColumn; set => Set(ref colorColumn, value); }

    /// <summary>Categories, ranges or literal colors; null chooses from the values.</summary>
    public MapColorMode? ColorMode { get => colorMode; set => Set(ref colorMode, value); }

    public int RangeCount { get => rangeCount; set => Set(ref rangeCount, Math.Clamp(value, 2, 10)); }

    /// <summary>Text column that draws one track per value (a vehicle ID, say).</summary>
    public string? SplitBy { get => splitBy; set => Set(ref splitBy, value); }

    public bool CombineResults { get => combineResults; set => Set(ref combineResults, value); }

    public double LineWidth { get => lineWidth; set => Set(ref lineWidth, Math.Clamp(value, 1, 16)); }

    public string Background { get => background; set => Set(ref background, MapBackground.Get(value).Name); }

    public bool ShowEndpoints { get => showEndpoints; set => Set(ref showEndpoints, value); }

    /// <summary>Colors picked for legend entries (#RRGGBB), keyed by <see cref="Key"/>.</summary>
    public IReadOnlyDictionary<string, string> Colors => colors;

    /// <summary>Color rules over several fields (used when <see cref="ColorMode"/> is Rules). Call <see cref="RulesChanged"/> after editing.</summary>
    public List<MapColorRule> Rules { get; } = new();

    /// <summary>Color of points that match no rule.</summary>
    public string OtherColor { get => otherColor; set => Set(ref otherColor, value); }

    public void RulesChanged() => Version++;

    /// <summary>Overlay layers (operatingmode = 2, depthguidance = on...), all shown at once. Call <see cref="LayersChanged"/> after editing.</summary>
    public List<MapLayer> Layers { get; } = new();

    public void LayersChanged() => Version++;

    public static string Key(string? colorColumn, string label) => (colorColumn ?? "\u0001tracks") + "\u001f" + label;

    public void SetColor(string key, string? hex)
    {
        if (hex == null ? colors.Remove(key) : !colors.TryGetValue(key, out var old) || old != hex)
        {
            if (hex != null) colors[key] = hex;
            Version++;
        }
    }

    public void ResetColors()
    {
        if (colors.Count == 0) return;
        colors.Clear();
        Version++;
    }

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Version++;
    }
}

/// <summary>
/// Draws a result's latitude/longitude columns as lines on a map. The user binds the latitude and longitude
/// columns, an optional column that colors the line (categories, value ranges or literal colors), and the
/// color of each legend entry.
/// </summary>
public sealed class MapView : UserControl
{
    private static readonly IBrush Accent = Brush.Parse("#1a65a7");
    private static readonly string[] Palette =
    {
        // Blue first: a single track, the first value and the first rule are blue by default.
        "#2979FF", "#FFD400", "#FF6D00", "#00E5FF", "#FF1744", "#76FF03", "#D500F9", "#FFFFFF",
        "#FFAB00", "#1DE9B6", "#F50057", "#C6FF00"
    };
    private static readonly string[] PresetColors =
    {
        "#FFD400", "#FFAB00", "#FF6D00", "#FF1744", "#D50000", "#F50057", "#D500F9", "#2979FF",
        "#00B0FF", "#00E5FF", "#1DE9B6", "#00C853", "#76FF03", "#C6FF00", "#FFFFFF", "#000000"
    };
    private static readonly double[] Widths = { 1, 2, 3, 4, 5, 6, 8 };
    private static readonly string[] ModeNames = { "Automatic", "Categories", "Ranges", "Colors in column" };
    private const string NoneItem = "(none)";
    private const string RulesItem = "Rules (multiple fields)…";

    private readonly MapSettings settings;
    private readonly MapCanvas canvas = new();
    private readonly ComboBox latBox = new() { MinWidth = 120 };
    private readonly ComboBox lonBox = new() { MinWidth = 120 };
    private readonly ComboBox colorBox = new() { MinWidth = 130 };
    private readonly ComboBox modeBox = new() { ItemsSource = ModeNames, MinWidth = 110 };
    private readonly ComboBox rangeBox = new() { ItemsSource = Enumerable.Range(2, 9).Select(i => $"{i} ranges").ToList(), MinWidth = 96 };
    private readonly Button colorsButton = new() { Content = "Colors ▾", Padding = new Thickness(8, 3) };
    private readonly StackPanel colorsPanel = new() { Spacing = 4, Margin = new Thickness(6), MinWidth = 300 };
    private readonly Button layersButton = new() { Content = "Layers ▾", Padding = new Thickness(8, 3) };
    private readonly StackPanel layersPanel = new() { Spacing = 4, Margin = new Thickness(6), MinWidth = 420 };
    private string? layersPanelKey;
    private static readonly string[] LayerPalette =
    {
        // Not blue first: layers must stand out against the (blue) track.
        "#FF9100", "#00E676", "#FF1744", "#D500F9", "#00E5FF", "#FFEA00", "#FFFFFF", "#C6FF00", "#F50057", "#651FFF", "#1DE9B6", "#2979FF"
    };
    private static readonly string[] LayerStyleNames = { "Line", "Halo (under track)", "Dashed", "Points" };
    private static readonly double[] LayerWidths = { 1, 2, 3, 4, 5, 6, 8, 10 };
    private readonly ComboBox widthBox = new() { ItemsSource = Widths.Select(w => w.ToString("0", CultureInfo.CurrentCulture) + " px").ToList(), MinWidth = 72 };
    private readonly TextBlock splitLabel = Label("Track by");
    private readonly ComboBox splitBox = new() { MinWidth = 120 };
    private readonly CheckBox combineBox = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox backgroundBox = new() { ItemsSource = MapBackground.All.Select(b => b.Name).ToList(), MinWidth = 100 };
    private readonly CheckBox endpointsBox = new() { Content = "Start/end", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock status = new() { FontSize = 12, Foreground = Accent, Margin = new Thickness(2, 6, 2, 0), TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel legendRows = new() { Spacing = 3 };
    private readonly Border legend;
    private readonly StackPanel hoverRows = new() { Spacing = 2 };
    private readonly Border hover;
    private readonly TextBlock emptyText = new()
    {
        FontSize = 13, Foreground = Accent, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(24), IsHitTestVisible = false
    };
    private readonly Border emptyCard;
    private readonly StackPanel zoomButtons;
    private readonly Grid surface = new();

    private ResultSeries? current;
    private IReadOnlyList<ResultSeries>? all;
    private MapData? data;
    private string? fittedKey;
    private int renderedVersion = -1;
    private int buildId;
    private bool binding;
    private string? colorsPanelKey;
    private bool openRulesAfterRefresh;

    public MapView(MapSettings settings)
    {
        this.settings = settings;

        legend = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#EEFFFFFF")),
            BorderBrush = Brush.Parse("#c9d9e8"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6), Margin = new Thickness(10),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Child = legendRows, IsVisible = false
        };
        hover = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F7FFFFFF")),
            BorderBrush = Brush.Parse("#c9d9e8"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false, IsVisible = false, Child = hoverRows
        };
        emptyCard = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#E6FFFFFF")), CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = emptyText, IsVisible = false, IsHitTestVisible = false
        };
        zoomButtons = new StackPanel
        {
            Spacing = 4, Margin = new Thickness(10, 10, 10, 30),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom
        };
        zoomButtons.Children.Add(MapButton("+", "Zoom in", () => canvas.ZoomBy(1)));
        zoomButtons.Children.Add(MapButton("−", "Zoom out", () => canvas.ZoomBy(-1)));
        zoomButtons.Children.Add(MapButton("⤢", "Fit all tracks", canvas.FitToData));

        surface.Children.Add(canvas);
        surface.Children.Add(legend);
        surface.Children.Add(emptyCard);
        surface.Children.Add(zoomButtons);
        surface.Children.Add(hover);
        canvas.HoverMoved += OnHover;

        latBox.SelectionChanged += (_, _) => { if (!binding && latBox.SelectedItem is string s) { settings.LatitudeColumn = s; _ = RefreshAsync(); } };
        lonBox.SelectionChanged += (_, _) => { if (!binding && lonBox.SelectedItem is string s) { settings.LongitudeColumn = s; _ = RefreshAsync(); } };
        colorBox.SelectionChanged += (_, _) =>
        {
            if (binding) return;
            if (colorBox.SelectedItem as string == RulesItem)
            {
                settings.ColorColumn = null;
                settings.ColorMode = MapColorMode.Rules;
                if (settings.Rules.Count == 0) settings.Rules.Add(NewRule());
                settings.RulesChanged();
                openRulesAfterRefresh = true;
            }
            else
            {
                settings.ColorColumn = colorBox.SelectedIndex > 0 ? colorBox.SelectedItem as string : null;
                settings.ColorMode = null;
            }
            _ = RefreshAsync();
        };
        modeBox.SelectionChanged += (_, _) =>
        {
            if (binding || modeBox.SelectedIndex < 0) return;
            settings.ColorMode = modeBox.SelectedIndex switch { 1 => MapColorMode.Categories, 2 => MapColorMode.Ranges, 3 => MapColorMode.Colors, _ => null };
            _ = RefreshAsync();
        };
        rangeBox.SelectionChanged += (_, _) =>
        {
            if (binding || rangeBox.SelectedIndex < 0) return;
            settings.RangeCount = rangeBox.SelectedIndex + 2;
            _ = RefreshAsync();
        };
        splitBox.SelectionChanged += (_, _) =>
        {
            if (binding) return;
            settings.SplitBy = splitBox.SelectedIndex > 0 ? splitBox.SelectedItem as string : null;
            _ = RefreshAsync();
        };
        combineBox.IsCheckedChanged += (_, _) =>
        {
            if (binding) return;
            settings.CombineResults = combineBox.IsChecked == true;
            _ = RefreshAsync();
        };
        widthBox.SelectionChanged += (_, _) =>
        {
            if (binding || widthBox.SelectedIndex < 0) return;
            settings.LineWidth = Widths[widthBox.SelectedIndex];
            Restyle();
        };
        backgroundBox.SelectionChanged += (_, _) =>
        {
            if (binding || backgroundBox.SelectedItem is not string name) return;
            settings.Background = name;
            Restyle();
        };
        endpointsBox.IsCheckedChanged += (_, _) =>
        {
            if (binding) return;
            settings.ShowEndpoints = endpointsBox.IsChecked == true;
            Restyle();
        };
        colorsButton.Flyout = new Flyout { Content = new ScrollViewer { MaxHeight = 460, Content = colorsPanel }, Placement = PlacementMode.BottomEdgeAlignedLeft };
        layersButton.Flyout = new Flyout { Content = new ScrollViewer { MaxHeight = 560, Content = layersPanel }, Placement = PlacementMode.BottomEdgeAlignedLeft };
        ToolTip.SetTip(layersButton, "Overlays shown together on the track, each for a condition on any fields (operating mode, depth guidance, bunker unload...)");

        ToolTip.SetTip(latBox, "Column holding the latitude (−90 … 90)");
        ToolTip.SetTip(lonBox, "Column holding the longitude (−180 … 180)");
        ToolTip.SetTip(colorBox, "Column that sets the line color (a status, speed, or #RRGGBB colors), or Rules to combine several fields");
        ToolTip.SetTip(modeBox, "Categories: one color per value. Ranges: numeric values in equal ranges. Colors in column: the values are colors.");
        ToolTip.SetTip(colorsButton, "Pick the color of each value, range or track");
        ToolTip.SetTip(splitBox, "Draw a separate track for each value of a text column (a vehicle or device ID)");
        ToolTip.SetTip(backgroundBox, "Map imagery. None works offline.");
        ToolTip.SetTip(endpointsBox, "Mark where each track starts and ends");

        var fit = new Button { Content = "Fit", Padding = new Thickness(8, 3) };
        ToolTip.SetTip(fit, "Show all tracks. Drag to pan, scroll or double-click to zoom.");
        fit.Click += (_, _) => canvas.FitToData();
        var save = new Button { Content = "Save PNG…", Padding = new Thickness(8, 3) };
        save.Click += async (_, _) => await SavePngAsync();

        var toolbar = new ResponsiveToolBar();
        toolbar.Add(Label("Latitude"), latBox);
        toolbar.Add(Label("Longitude"), lonBox);
        toolbar.Add(Label("Color by"), colorBox);
        toolbar.Add(modeBox);
        toolbar.Add(rangeBox);
        toolbar.Add(colorsButton);
        toolbar.Add(layersButton);
        toolbar.Add(Label("Width"), widthBox);
        toolbar.Add(splitLabel, splitBox);
        toolbar.Add(combineBox);
        toolbar.Add(Label("Map"), backgroundBox);
        toolbar.Add(endpointsBox);
        toolbar.Add(fit);
        toolbar.Add(save);

        var layout = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(status, Dock.Bottom);
        layout.Children.Add(toolbar);
        layout.Children.Add(status);
        layout.Children.Add(new Border
        {
            BorderBrush = Brush.Parse("#e5eef5"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
            // The query splitter can leave less than 200px for the map. A minimum height here
            // makes Avalonia center the oversized surface over the toolbar above it.
            ClipToBounds = true, Child = surface
        });
        Content = layout;
        Restyle();
    }

    /// <summary>The map control (exposed for tests).</summary>
    public MapCanvas Canvas => canvas;

    /// <summary>The tracks currently drawn (exposed for tests).</summary>
    public MapData? Data => data;

    public void SetResults(ResultSeries result, IReadOnlyList<ResultSeries>? allResults)
    {
        current = result;
        all = allResults;
        renderedVersion = -1;
    }

    /// <summary>Rebuilds when settings changed in another result tab since this map was drawn.</summary>
    public Task EnsureCurrentAsync() => renderedVersion == settings.Version ? Task.CompletedTask : RefreshAsync();

    /// <summary>Rebuilds the tracks (off the UI thread) and redraws.</summary>
    public async Task RefreshAsync()
    {
        if (current == null) return;
        var id = ++buildId;
        var sources = Sources();
        var options = new MapOptions
        {
            LatitudeColumn = settings.LatitudeColumn,
            LongitudeColumn = settings.LongitudeColumn,
            ColorColumn = settings.ColorColumn,
            ColorMode = settings.ColorMode,
            RangeCount = settings.RangeCount,
            SplitBy = settings.SplitBy,
            // A copy: the editor may change the rules while the map is being built.
            Rules = settings.ColorMode == MapColorMode.Rules ? settings.Rules.Select(CopyRule).ToList() : null,
            OtherColor = settings.OtherColor,
            Layers = settings.Layers.Select(l => l.Copy()).ToList()
        };
        status.Text = "Preparing map…";
        MapData built;
        try
        {
            built = await Task.Run(() => MapTrackBuilder.Build(sources, options));
        }
        catch (Exception ex)
        {
            built = new MapData { Message = "Could not map this result: " + ex.Message };
        }
        if (id != buildId) return;
        data = built;
        renderedVersion = settings.Version;
        BindToolbar(built, sources);

        // Re-fit only when the tracks themselves changed, not when the coloring did.
        var key = $"{built.LatitudeColumn}|{built.LongitudeColumn}|{settings.SplitBy}|{sources.Count}|{built.PointCount}|{built.Bounds}";
        var fit = key != fittedKey;
        fittedKey = key;
        canvas.SetData(built, fit);
        Restyle();
        if (openRulesAfterRefresh)
        {
            openRulesAfterRefresh = false;
            colorsButton.Flyout?.ShowAt(colorsButton);
        }
    }

    private IReadOnlyList<ResultSeries> Sources() =>
        settings.CombineResults && all is { Count: > 1 } ? all : new[] { current! };

    private void BindToolbar(MapData map, IReadOnlyList<ResultSeries> sources)
    {
        binding = true;
        try
        {
            latBox.ItemsSource = map.CoordinateColumns;
            latBox.SelectedItem = map.LatitudeColumn;
            lonBox.ItemsSource = map.CoordinateColumns;
            lonBox.SelectedItem = map.LongitudeColumn;

            var colorItems = new List<string> { NoneItem };
            colorItems.AddRange(map.ColorColumns);
            if (map.AllColumns.Count > 0) colorItems.Add(RulesItem);
            colorBox.ItemsSource = colorItems;
            colorBox.SelectedIndex = settings.ColorMode == MapColorMode.Rules && map.AllColumns.Count > 0 ? colorItems.Count - 1
                : map.ColorColumn != null ? Math.Max(0, colorItems.IndexOf(map.ColorColumn)) : 0;
            modeBox.IsVisible = map.ColorColumn != null;
            modeBox.SelectedIndex = settings.ColorMode switch
            {
                MapColorMode.Categories => 1, MapColorMode.Ranges => 2, MapColorMode.Colors => 3, _ => 0
            };
            rangeBox.IsVisible = map.ColorMode == MapColorMode.Ranges;
            rangeBox.SelectedIndex = Math.Clamp(settings.RangeCount, 2, 10) - 2;
            var rules = settings.ColorMode == MapColorMode.Rules;
            colorsButton.Content = rules ? $"Rules ({settings.Rules.Count}) ▾" : "Colors ▾";
            colorsButton.IsVisible = rules || map.Legend.Count > 0;
            ToolTip.SetTip(colorsButton, rules ? "Edit the color rules" : "Pick the color of each value, range or track");

            var splitItems = new List<string> { NoneItem };
            splitItems.AddRange(map.SplitCandidates);
            splitBox.ItemsSource = splitItems;
            splitBox.SelectedIndex = settings.SplitBy != null
                ? Math.Max(0, splitItems.FindIndex(s => string.Equals(s, settings.SplitBy, StringComparison.OrdinalIgnoreCase)))
                : 0;
            splitLabel.IsVisible = splitBox.IsVisible = map.SplitCandidates.Count > 0;

            var total = all?.Count ?? 1;
            combineBox.IsVisible = total > 1;
            combineBox.Content = $"Combine all {total} results";
            combineBox.IsChecked = settings.CombineResults;

            widthBox.SelectedIndex = NearestWidthIndex(settings.LineWidth);
            backgroundBox.SelectedItem = MapBackground.Get(settings.Background).Name;
            endpointsBox.IsChecked = settings.ShowEndpoints;
        }
        finally
        {
            binding = false;
        }
        // Rebuild the editor only when its shape changes, so editing a rule never loses the field being typed in.
        var key = settings.ColorMode == MapColorMode.Rules
            ? "rules|" + string.Join("|", map.AllColumns)
            : $"{map.ColorColumn}|{map.ColorMode}|" + string.Join("|", map.Legend.Select(l => l.Label + "#" + l.Count));
        if (key != colorsPanelKey) BuildColorsPanel();
        layersButton.Content = settings.Layers.Count > 0 ? $"Layers ({settings.Layers.Count}) ▾" : "Layers ▾";
        if (string.Join("|", map.AllColumns) != layersPanelKey) BuildLayersPanel();
    }

    /// <summary>Applies color, width and background changes without rebuilding the tracks or moving the map.</summary>
    private void Restyle()
    {
        if (data != null && renderedVersion >= 0) renderedVersion = settings.Version;
        canvas.SetStyle(ColorOf, settings.LineWidth, MapBackground.Get(settings.Background), settings.ShowEndpoints);
        BuildLegend();
        var map = data;
        var drawable = map is { PointCount: > 0 };
        emptyCard.IsVisible = map != null && !drawable;
        emptyText.Text = map?.Message ?? "";
        legend.IsVisible = drawable && (map!.Legend.Count > 0 || map.Layers.Count > 0);
        status.Text = map == null ? "" : Summary(map);
    }

    private Color ColorOf(int legendIndex)
    {
        var map = data;
        if (map == null || legendIndex < 0 || legendIndex >= map.Legend.Count) return Color.Parse(Palette[0]);
        var entry = map.Legend[legendIndex];
        if (map.ColorMode != MapColorMode.Rules &&
            settings.Colors.TryGetValue(MapSettings.Key(map.ColorColumn, entry.Label), out var hex) && Color.TryParse(hex, out var picked)) return picked;
        if (entry.DefaultColor != null && Color.TryParse(entry.DefaultColor, out var preset)) return preset;
        return Color.Parse(Palette[legendIndex % Palette.Length]);
    }

    private void BuildLegend()
    {
        legendRows.Children.Clear();
        var map = data;
        if (map == null) return;
        var title = map.ColorMode == MapColorMode.Rules ? "Rules" : map.ColorColumn ?? (map.Tracks.Count > 1 ? "Tracks" : null);
        if (title != null)
            legendRows.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Accent, Margin = new Thickness(0, 0, 0, 2) });
        const int max = 14;
        for (var i = 0; i < Math.Min(max, map.Legend.Count); i++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new Border { Width = 18, Height = 5, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(ColorOf(i)) });
            row.Children.Add(new TextBlock { Text = map.Legend[i].Label, FontSize = 12, Foreground = Brush.Parse("#33536F"), MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis });
            legendRows.Children.Add(row);
        }
        if (map.Legend.Count > max)
            legendRows.Children.Add(new TextBlock { Text = $"+{map.Legend.Count - max} more (Colors ▾)", FontSize = 11, Foreground = Accent });

        // Layers: tick to show or hide each one.
        if (map.Layers.Count == 0) return;
        legendRows.Children.Add(new TextBlock { Text = "Layers", FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Accent, Margin = new Thickness(0, 6, 0, 0) });
        for (var l = 0; l < map.Layers.Count && l < settings.Layers.Count; l++)
        {
            var layer = settings.Layers[l];
            var shown = map.Layers[l];
            var check = new CheckBox { IsChecked = layer.Visible, Padding = new Thickness(4, 0), MinHeight = 20, FontSize = 12 };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(LayerSample(shown));
            content.Children.Add(new TextBlock
            {
                Text = $"{shown.Describe()}  ({(l < map.LayerCounts.Count ? map.LayerCounts[l] : 0).ToString("N0", CultureInfo.CurrentCulture)})",
                FontSize = 12, Foreground = Brush.Parse("#33536F"), MaxWidth = 240, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
            });
            check.Content = content;
            check.IsCheckedChanged += (_, _) =>
            {
                if (layer.Visible == (check.IsChecked == true)) return;
                layer.Visible = check.IsChecked == true;
                LayersEdited(rebuild: true);
            };
            legendRows.Children.Add(check);
        }
    }

    /// <summary>A small picture of how a layer is drawn, for the legend.</summary>
    private static Control LayerSample(MapLayer layer)
    {
        var brush = BrushOf(layer.Color);
        return layer.Style switch
        {
            MapLayerStyle.Points => new Border { Width = 18, Height = 10, Child = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(5), Background = brush, BorderBrush = Brushes.White, BorderThickness = new Thickness(1) } },
            MapLayerStyle.Halo => new Border { Width = 18, Height = 10, CornerRadius = new CornerRadius(4), Background = brush, Opacity = 0.85, VerticalAlignment = VerticalAlignment.Center,
                Child = new Border { Height = 3, Background = Brush.Parse("#2979FF"), VerticalAlignment = VerticalAlignment.Center } },
            MapLayerStyle.Dashed => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center,
                Children = { new Border { Width = 5, Height = 4, Background = brush }, new Border { Width = 5, Height = 4, Background = brush }, new Border { Width = 4, Height = 4, Background = brush } } },
            _ => new Border { Width = 18, Height = 5, CornerRadius = new CornerRadius(2), Background = brush, VerticalAlignment = VerticalAlignment.Center }
        };
    }

    // ------------------------------------------------------------------ layers (several fields at once)

    /// <summary>Layers ▾: any number of overlays, each a condition over any fields, drawn together on the track.</summary>
    private void BuildLayersPanel()
    {
        layersPanel.Children.Clear();
        var map = data;
        layersPanelKey = map == null ? null : string.Join("|", map.AllColumns);
        var columns = map?.AllColumns.ToList() ?? new List<string>();
        layersPanel.Children.Add(new TextBlock
        {
            Text = "Layers mark where a condition holds, on top of the track's colors. Any number show at once, and a point can be in several " +
                   "(for example operatingmode = 2 as a blue line, depthguidance = on as a green halo, bunkerunload = 1 as points).",
            FontSize = 12, Foreground = Accent, TextWrapping = TextWrapping.Wrap, MaxWidth = 560, Margin = new Thickness(0, 0, 0, 4)
        });
        for (var i = 0; i < settings.Layers.Count; i++) layersPanel.Children.Add(LayerCard(settings.Layers[i], i, columns));
        if (settings.Layers.Count >= MapTrackBuilder.MaxLayers)
            layersPanel.Children.Add(new TextBlock { Text = $"A map can show up to {MapTrackBuilder.MaxLayers} layers.", FontSize = 11, Foreground = Accent });

        var add = new Button { Content = "+ Add layer", Padding = new Thickness(8, 3), FontSize = 12, IsEnabled = settings.Layers.Count < MapTrackBuilder.MaxLayers };
        add.Click += (_, _) =>
        {
            settings.Layers.Add(NewLayer(DefaultRuleColumn(), null, null));
            LayersEdited(rebuild: true);
        };

        // Quick start: one layer per value of a field (each operating mode in its own color, say).
        var perValueColumn = new ComboBox { ItemsSource = map?.ColorColumns ?? Array.Empty<string>(), MinWidth = 140, FontSize = 12 };
        var perValue = new Button { Content = "Add", Padding = new Thickness(8, 3), FontSize = 12 };
        ToolTip.SetTip(perValue, "Adds a layer (field = value) for each value the field takes in this result, up to 12");
        perValue.Click += (_, _) =>
        {
            if (perValueColumn.SelectedItem is not string column || current == null) return;
            var values = MapTrackBuilder.DistinctValues(Sources(), column, 12);
            foreach (var value in values.Take(12))
            {
                if (settings.Layers.Count >= MapTrackBuilder.MaxLayers) break;
                settings.Layers.Add(NewLayer(column, value, $"{column} = {value}"));
            }
            if (values.Count > 12) status.Text = $"{column} has more than 12 values; added the first 12.";
            LayersEdited(rebuild: true);
        };
        var quick = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
        quick.Children.Add(add);
        quick.Children.Add(new TextBlock { Text = "or one layer per value of", FontSize = 12, Foreground = Accent, VerticalAlignment = VerticalAlignment.Center });
        quick.Children.Add(perValueColumn);
        quick.Children.Add(perValue);
        layersPanel.Children.Add(quick);

        if (settings.Layers.Count > 0)
        {
            var clear = new Button { Content = "Remove all layers", Padding = new Thickness(8, 3), FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
            clear.Click += (_, _) =>
            {
                settings.Layers.Clear();
                LayersEdited(rebuild: true);
            };
            layersPanel.Children.Add(clear);
        }
    }

    private Control LayerCard(MapLayer layer, int index, List<string> columns)
    {
        var stack = new StackPanel { Spacing = 4 };

        var visible = new CheckBox { IsChecked = layer.Visible, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0, Padding = new Thickness(0) };
        ToolTip.SetTip(visible, "Show this layer");
        visible.IsCheckedChanged += (_, _) =>
        {
            if (layer.Visible == (visible.IsChecked == true)) return;
            layer.Visible = visible.IsChecked == true;
            LayersEdited(rebuild: false);
        };

        var swatch = Swatch(layer.Color);
        var swatchButton = new Button { Content = swatch, Padding = new Thickness(3), Background = Brushes.Transparent };
        ToolTip.SetTip(swatchButton, "Pick this layer's color");
        var picker = ColorPicker(layer.Color, hex =>
        {
            if (hex == null) return;
            layer.Color = hex;
            swatch.Background = BrushOf(hex);
            LayersEdited(rebuild: false);
        }, allowAutomatic: false);
        swatchButton.Click += (_, _) => picker.IsVisible = !picker.IsVisible;

        var name = new TextBox { Text = layer.Name, Watermark = "Name, e.g. Depth guidance", Width = 190, FontSize = 12 };
        OnCommit(name, () =>
        {
            var text = string.IsNullOrWhiteSpace(name.Text) ? null : name.Text.Trim();
            if (text == layer.Name) return;
            layer.Name = text;
            LayersEdited(rebuild: false);
        });

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        header.Children.Add(visible);
        header.Children.Add(swatchButton);
        header.Children.Add(name);
        header.Children.Add(SmallButton("↑", "Move up (drawn earlier, under later layers)", index > 0, () => MoveLayer(index, -1)));
        header.Children.Add(SmallButton("↓", "Move down (drawn later, on top)", index < settings.Layers.Count - 1, () => MoveLayer(index, 1)));
        header.Children.Add(SmallButton("✕", "Delete layer", true, () =>
        {
            settings.Layers.RemoveAt(index);
            LayersEdited(rebuild: true);
        }));
        stack.Children.Add(header);
        stack.Children.Add(picker);

        var style = new ComboBox { ItemsSource = LayerStyleNames, SelectedIndex = (int)layer.Style, MinWidth = 140, FontSize = 12 };
        ToolTip.SetTip(style, "Line / Dashed: drawn over the track. Halo: a wide band under the track, so both colors show. Points: a dot at each matching point.");
        style.SelectionChanged += (_, _) =>
        {
            if (style.SelectedIndex < 0 || (MapLayerStyle)style.SelectedIndex == layer.Style) return;
            layer.Style = (MapLayerStyle)style.SelectedIndex;
            LayersEdited(rebuild: false);
        };
        var width = new ComboBox
        {
            ItemsSource = LayerWidths.Select(w => w.ToString("0", CultureInfo.CurrentCulture) + " px").ToList(),
            SelectedIndex = Array.IndexOf(LayerWidths, LayerWidths.OrderBy(w => Math.Abs(w - layer.Width)).First()), MinWidth = 72, FontSize = 12
        };
        width.SelectionChanged += (_, _) =>
        {
            if (width.SelectedIndex < 0 || LayerWidths[width.SelectedIndex] == layer.Width) return;
            layer.Width = LayerWidths[width.SelectedIndex];
            LayersEdited(rebuild: false);
        };
        var styleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(34, 0, 0, 0) };
        styleRow.Children.Add(Label("Style"));
        styleRow.Children.Add(style);
        styleRow.Children.Add(Label("Width"));
        styleRow.Children.Add(width);
        stack.Children.Add(styleRow);

        for (var c = 0; c < layer.Conditions.Count; c++) stack.Children.Add(ConditionRow(layer.Conditions, c, columns, LayersEdited));
        var addCondition = new Button { Content = layer.Conditions.Count == 0 ? "+ Condition" : "+ And condition", Padding = new Thickness(6, 2), FontSize = 11, Margin = new Thickness(34, 0, 0, 0) };
        addCondition.Click += (_, _) =>
        {
            layer.Conditions.Add(new MapCondition { Column = DefaultRuleColumn() });
            LayersEdited(rebuild: true);
        };
        stack.Children.Add(addCondition);

        return new Border
        {
            Background = Brush.Parse("#f7fafd"), BorderBrush = Brush.Parse("#d9e5f0"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5), Padding = new Thickness(6), Margin = new Thickness(0, 0, 0, 4), Child = stack
        };
    }

    private void MoveLayer(int index, int delta)
    {
        var target = index + delta;
        if (target < 0 || target >= settings.Layers.Count) return;
        (settings.Layers[index], settings.Layers[target]) = (settings.Layers[target], settings.Layers[index]);
        LayersEdited(rebuild: true);
    }

    private void LayersEdited(bool rebuild)
    {
        settings.LayersChanged();
        if (rebuild) BuildLayersPanel();
        layersButton.Content = settings.Layers.Count > 0 ? $"Layers ({settings.Layers.Count}) ▾" : "Layers ▾";
        _ = RefreshAsync();
    }

    private MapLayer NewLayer(string? column, string? value, string? name) => new()
    {
        Name = name,
        Color = LayerPalette[settings.Layers.Count % LayerPalette.Length],
        Conditions = { new MapCondition { Column = column, Operator = MapConditionOperator.Equals, Value = value } }
    };

    /// <summary>Colors ▾: one row per value, range or track, with a swatch that opens a picker.</summary>
    private void BuildColorsPanel()
    {
        colorsPanel.Children.Clear();
        var map = data;
        colorsPanelKey = null;
        if (map == null) return;
        colorsPanelKey = settings.ColorMode == MapColorMode.Rules
            ? "rules|" + string.Join("|", map.AllColumns)
            : $"{map.ColorColumn}|{map.ColorMode}|" + string.Join("|", map.Legend.Select(l => l.Label + "#" + l.Count));
        if (settings.ColorMode == MapColorMode.Rules)
        {
            BuildRulesPanel(map);
            return;
        }
        colorsPanel.Children.Add(new TextBlock
        {
            Text = map.ColorColumn != null ? $"Line color by {map.ColorColumn}" : "Line color per track",
            FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Accent, Margin = new Thickness(0, 0, 0, 4)
        });
        for (var i = 0; i < map.Legend.Count; i++) colorsPanel.Children.Add(ColorRow(map, i));
        var reset = new Button { Content = "Reset colors", Padding = new Thickness(8, 3), Margin = new Thickness(0, 6, 0, 0) };
        reset.Click += (_, _) =>
        {
            settings.ResetColors();
            Restyle();
            BuildColorsPanel();
        };
        colorsPanel.Children.Add(reset);
    }

    private Control ColorRow(MapData map, int index)
    {
        var entry = map.Legend[index];
        var key = MapSettings.Key(map.ColorColumn, entry.Label);
        var swatch = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(ColorOf(index)), BorderBrush = Brush.Parse("#9fb3c6"), BorderThickness = new Thickness(1) };
        var swatchButton = new Button { Content = swatch, Padding = new Thickness(3), Background = Brushes.Transparent };
        ToolTip.SetTip(swatchButton, "Pick a color");
        var name = new TextBlock
        {
            Text = $"{entry.Label}  ({entry.Count.ToString("N0", CultureInfo.CurrentCulture)})", FontSize = 12,
            Foreground = Brush.Parse("#33536F"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 280,
            FontWeight = settings.Colors.ContainsKey(key) ? FontWeight.SemiBold : FontWeight.Normal
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(swatchButton);
        row.Children.Add(name);

        // The picker opens inside the menu (a second pop-up would close the first).
        var picker = ColorPicker(settings.Colors.TryGetValue(key, out var currentHex) ? currentHex : null, hex =>
        {
            settings.SetColor(key, hex);
            swatch.Background = new SolidColorBrush(ColorOf(index));
            name.FontWeight = settings.Colors.ContainsKey(key) ? FontWeight.SemiBold : FontWeight.Normal;
            Restyle();
        }, allowAutomatic: true);
        swatchButton.Click += (_, _) => picker.IsVisible = !picker.IsVisible;

        var block = new StackPanel { Spacing = 2 };
        block.Children.Add(row);
        block.Children.Add(picker);
        return block;
    }

    // ------------------------------------------------------------------ color rules (several fields)

    /// <summary>Rules ▾: ordered rules, each a color and one or more conditions joined by AND; the first match wins.</summary>
    private void BuildRulesPanel(MapData map)
    {
        colorsPanel.Children.Add(new TextBlock
        {
            Text = "Color rules: a point takes the color of the first rule it meets.\nConditions in a rule must all be true (AND).",
            FontSize = 12, Foreground = Accent, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, Margin = new Thickness(0, 0, 0, 4)
        });
        var columns = map.AllColumns.ToList();
        for (var i = 0; i < settings.Rules.Count; i++) colorsPanel.Children.Add(RuleCard(settings.Rules[i], i, columns));

        var add = new Button { Content = "+ Add rule", Padding = new Thickness(8, 3), FontSize = 12 };
        add.Click += (_, _) =>
        {
            settings.Rules.Add(NewRule());
            RulesEdited(rebuild: true);
        };
        colorsPanel.Children.Add(add);

        // Points that meet no rule.
        var otherSwatch = Swatch(settings.OtherColor);
        var otherButton = new Button { Content = otherSwatch, Padding = new Thickness(3), Background = Brushes.Transparent };
        var otherPicker = ColorPicker(settings.OtherColor, hex =>
        {
            settings.OtherColor = hex ?? "#9E9E9E";
            otherSwatch.Background = BrushOf(settings.OtherColor);
            RulesEdited(rebuild: false);
        }, allowAutomatic: false);
        otherButton.Click += (_, _) => otherPicker.IsVisible = !otherPicker.IsVisible;
        var otherRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 6, 0, 0) };
        otherRow.Children.Add(otherButton);
        otherRow.Children.Add(new TextBlock { Text = "Other points (no rule matched)", FontSize = 12, Foreground = Brush.Parse("#33536F"), VerticalAlignment = VerticalAlignment.Center });
        colorsPanel.Children.Add(otherRow);
        colorsPanel.Children.Add(otherPicker);
    }

    private Control RuleCard(MapColorRule rule, int index, List<string> columns)
    {
        var stack = new StackPanel { Spacing = 4 };

        var swatch = Swatch(rule.Color);
        var swatchButton = new Button { Content = swatch, Padding = new Thickness(3), Background = Brushes.Transparent };
        ToolTip.SetTip(swatchButton, "Pick this rule's color");
        var picker = ColorPicker(rule.Color, hex =>
        {
            if (hex == null) return;
            rule.Color = hex;
            swatch.Background = BrushOf(hex);
            RulesEdited(rebuild: false);
        }, allowAutomatic: false);
        swatchButton.Click += (_, _) => picker.IsVisible = !picker.IsVisible;

        var label = new TextBox { Text = rule.Label, Watermark = "Legend name (optional)", Width = 170, FontSize = 12 };
        OnCommit(label, () =>
        {
            var text = string.IsNullOrWhiteSpace(label.Text) ? null : label.Text.Trim();
            if (text == rule.Label) return;
            rule.Label = text;
            RulesEdited(rebuild: false);
        });

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        header.Children.Add(swatchButton);
        header.Children.Add(new TextBlock { Text = $"Rule {index + 1}", FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Accent, VerticalAlignment = VerticalAlignment.Center, Width = 46 });
        header.Children.Add(label);
        header.Children.Add(SmallButton("↑", "Move up (earlier rules win)", index > 0, () => MoveRule(index, -1)));
        header.Children.Add(SmallButton("↓", "Move down", index < settings.Rules.Count - 1, () => MoveRule(index, 1)));
        header.Children.Add(SmallButton("✕", "Delete rule", true, () =>
        {
            settings.Rules.RemoveAt(index);
            RulesEdited(rebuild: true);
        }));
        stack.Children.Add(header);
        stack.Children.Add(picker);

        for (var c = 0; c < rule.Conditions.Count; c++) stack.Children.Add(ConditionRow(rule.Conditions, c, columns, RulesEdited));
        if (rule.Conditions.Count == 0)
            stack.Children.Add(new TextBlock { Text = "No conditions: matches every point.", FontSize = 11, Foreground = Brush.Parse("#5C7A96"), Margin = new Thickness(34, 0, 0, 0) });

        var addCondition = new Button { Content = rule.Conditions.Count == 0 ? "+ Condition" : "+ And condition", Padding = new Thickness(6, 2), FontSize = 11, Margin = new Thickness(34, 0, 0, 0) };
        ToolTip.SetTip(addCondition, "Add a test on another (or the same) field; all of a rule's conditions must be true");
        addCondition.Click += (_, _) =>
        {
            rule.Conditions.Add(new MapCondition { Column = DefaultRuleColumn() });
            RulesEdited(rebuild: true);
        };
        stack.Children.Add(addCondition);

        return new Border
        {
            Background = Brush.Parse("#f7fafd"), BorderBrush = Brush.Parse("#d9e5f0"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5), Padding = new Thickness(6), Margin = new Thickness(0, 0, 0, 4), Child = stack
        };
    }

    private static Control ConditionRow(List<MapCondition> conditions, int index, List<string> columns, Action<bool> edited) =>
        ConditionEditor.Row(conditions, index, columns, edited);

    private void MoveRule(int index, int delta)
    {
        var target = index + delta;
        if (target < 0 || target >= settings.Rules.Count) return;
        (settings.Rules[index], settings.Rules[target]) = (settings.Rules[target], settings.Rules[index]);
        RulesEdited(rebuild: true);
    }

    private void RulesEdited(bool rebuild)
    {
        settings.RulesChanged();
        if (rebuild) BuildColorsPanel();
        colorsButton.Content = $"Rules ({settings.Rules.Count}) ▾";
        _ = RefreshAsync();
    }

    private MapColorRule NewRule() => new()
    {
        Color = Palette[settings.Rules.Count % Palette.Length],
        Conditions = { new MapCondition { Column = DefaultRuleColumn() } }
    };

    /// <summary>A sensible first field for a new condition: a text field (status), else any non-position field.</summary>
    private string? DefaultRuleColumn() => data?.SplitCandidates.FirstOrDefault() ?? data?.ColorColumns.FirstOrDefault();

    private static MapColorRule CopyRule(MapColorRule rule) => new()
    {
        Color = rule.Color,
        Label = rule.Label,
        Conditions = rule.Conditions.Select(c => new MapCondition { Column = c.Column, Operator = c.Operator, Value = c.Value }).ToList()
    };

    private static void OnCommit(TextBox box, Action commit) => ConditionEditor.OnCommit(box, commit);

    private static Button SmallButton(string text, string tip, bool enabled, Action action) => ConditionEditor.SmallButton(text, tip, enabled, action);

    private static Border Swatch(string hex) => new()
    {
        Width = 18, Height = 18, CornerRadius = new CornerRadius(3), Background = BrushOf(hex),
        BorderBrush = Brush.Parse("#9fb3c6"), BorderThickness = new Thickness(1)
    };

    private static IBrush BrushOf(string? hex) => Color.TryParse(hex, out var color) ? new SolidColorBrush(color) : Brushes.Gray;

    /// <summary>Preset swatches plus a hex box; hidden until its swatch button is clicked.</summary>
    private static StackPanel ColorPicker(string? currentHex, Action<string?> apply, bool allowAutomatic)
    {
        var picker = new StackPanel { Spacing = 4, IsVisible = false, Margin = new Thickness(30, 2, 0, 6) };
        var swatches = new WrapPanel { MaxWidth = 300 };
        foreach (var color in PresetColors)
        {
            var hex = color;
            var choice = new Button
            {
                Width = 22, Height = 22, Padding = new Thickness(0), Margin = new Thickness(0, 0, 3, 3),
                Background = new SolidColorBrush(Color.Parse(hex)), BorderBrush = Brush.Parse("#c9d9e8"), BorderThickness = new Thickness(1)
            };
            ToolTip.SetTip(choice, hex);
            choice.Click += (_, _) => apply(hex);
            swatches.Children.Add(choice);
        }
        picker.Children.Add(swatches);
        var hexBox = new TextBox { Watermark = "#RRGGBB", Width = 110, FontSize = 12, Text = currentHex ?? "" };
        ToolTip.SetTip(hexBox, "A hex color such as #FF8800, or a name such as orange");
        var applyHex = new Button { Content = "Apply", Padding = new Thickness(8, 2), FontSize = 12 };
        void ApplyText()
        {
            if (ChartView.TryParseColor(hexBox.Text, out var parsed)) { hexBox.Text = ChartView.ToHex(parsed); apply(hexBox.Text); }
            else hexBox.Text = "";
        }
        applyHex.Click += (_, _) => ApplyText();
        hexBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ApplyText(); e.Handled = true; } };
        var custom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        custom.Children.Add(hexBox);
        custom.Children.Add(applyHex);
        if (allowAutomatic)
        {
            var auto = new Button { Content = "Automatic", Padding = new Thickness(8, 2), FontSize = 12 };
            ToolTip.SetTip(auto, "Go back to the default color");
            auto.Click += (_, _) => { hexBox.Text = ""; apply(null); };
            custom.Children.Add(auto);
        }
        picker.Children.Add(custom);
        return picker;
    }

    private void OnHover(Point? position)
    {
        var map = data;
        var hit = position is { } p && map != null ? canvas.HitTestPoint(p) : null;
        canvas.Highlight(hit);
        if (hit is not { } h || map == null)
        {
            hover.IsVisible = false;
            return;
        }
        var track = map.Tracks[h.Track];
        var point = track.Points[h.Point];
        hoverRows.Children.Clear();
        var heading = point.Time is { } time ? FormatTime(time) : track.Label;
        hoverRows.Children.Add(new TextBlock { Text = heading, FontWeight = FontWeight.SemiBold, FontSize = 12, Foreground = Accent, Margin = new Thickness(0, 0, 0, 2) });
        if (point.Time != null && map.Tracks.Count > 1) AddHoverRow("Track", track.Label);
        AddHoverRow(map.LatitudeColumn ?? "Latitude", point.Latitude.ToString("0.######", CultureInfo.CurrentCulture));
        AddHoverRow(map.LongitudeColumn ?? "Longitude", point.Longitude.ToString("0.######", CultureInfo.CurrentCulture));
        if (map.ColorColumn != null) AddHoverRow(map.ColorColumn, point.Value ?? "null", ColorOf(point.Legend));
        else if (map.ColorMode == MapColorMode.Rules) AddHoverRow("Rule", point.Value ?? "", ColorOf(point.Legend));
        for (var l = 0; l < map.Layers.Count; l++)
            if (point.InLayer(l) && map.Layers[l].Visible)
                AddHoverRow("Layer", map.Layers[l].Describe(), Color.TryParse(map.Layers[l].Color, out var lc) ? lc : null);
        hover.IsVisible = true;

        var width = hover.Bounds.Width > 0 ? hover.Bounds.Width : 220;
        var height = hover.Bounds.Height > 0 ? hover.Bounds.Height : 90;
        var at = position!.Value;
        var x = at.X + 16 + width > surface.Bounds.Width ? Math.Max(0, at.X - 16 - width) : at.X + 16;
        var y = at.Y + 16 + height > surface.Bounds.Height ? Math.Max(0, at.Y - 16 - height) : at.Y + 16;
        hover.Margin = new Thickness(x, y, 0, 0);
    }

    private void AddHoverRow(string label, string value, Color? color = null)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (color is { } c)
            line.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(c) });
        line.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Brush.Parse("#33536F") });
        line.Children.Add(new TextBlock { Text = value, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#10314F") });
        hoverRows.Children.Add(line);
    }

    private static string FormatTime(DateTime utc)
    {
        try
        {
            return TickbirdApp.Current.Formatter.Format("time", utc) + " UTC";
        }
        catch
        {
            return utc.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.CurrentCulture) + " UTC";
        }
    }

    private static string Summary(MapData map)
    {
        var culture = CultureInfo.CurrentCulture;
        if (map.PointCount == 0) return map.SkippedRows > 0 ? $"{map.SkippedRows.ToString("N0", culture)} row(s) without a usable position." : "";
        var text = $"{map.Tracks.Count} track(s) · {map.PointCount.ToString("N0", culture)} points";
        if (map.ColorMode == MapColorMode.Rules) text += $" · colored by {map.Legend.Count(l => l.Label != "Other")} matching rule(s)";
        if (map.ColorColumn != null) text += $" · colored by {map.ColorColumn} ({map.ColorMode switch { MapColorMode.Ranges => "ranges", MapColorMode.Colors => "colors in column", _ => "categories" }})";
        if (map.Layers.Count > 0) text += $" · {map.Layers.Count(l => l.Visible)}/{map.Layers.Count} layer(s) shown";
        if (map.SkippedRows > 0) text += $" · {map.SkippedRows.ToString("N0", culture)} row(s) skipped (no position, or 0,0)";
        return text;
    }

    private async Task SavePngAsync()
    {
        if (data is not { PointCount: > 0 }) return;
        var name = ResultExporter.SafeFileName($"{current?.Name ?? "query"}_map.png");
        var path = await FilePicker.SaveAsync(this, name, "PNG images", "*.png");
        if (path == null) return;
        try
        {
            hover.IsVisible = false;
            zoomButtons.IsVisible = false;
            var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            var size = surface.Bounds.Size;
            using var bitmap = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)(size.Width * scale)), Math.Max(1, (int)(size.Height * scale))), new Vector(96 * scale, 96 * scale));
            bitmap.Render(surface);
            bitmap.Save(path);
        }
        catch (Exception ex)
        {
            if (TopLevel.GetTopLevel(this) is Window w) await Dialogs.ShowExceptionAsync(w, ex);
        }
        finally
        {
            zoomButtons.IsVisible = true;
        }
    }

    private static int NearestWidthIndex(double width)
    {
        var best = 0;
        for (var i = 1; i < Widths.Length; i++)
            if (Math.Abs(Widths[i] - width) < Math.Abs(Widths[best] - width)) best = i;
        return best;
    }

    private static Button MapButton(string text, string tip, Action action)
    {
        var button = new Button
        {
            Content = text, Width = 32, Height = 32, Padding = new Thickness(0), FontSize = 16, FontWeight = FontWeight.SemiBold,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.White, Foreground = Accent, BorderBrush = Brush.Parse("#c9d9e8"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4)
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => action();
        return button;
    }

    private static TextBlock Label(string text) => new() { Text = text, Foreground = Accent, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
}
