using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Tickbird.Core.Charting;
using Tickbird.Core.Data;
using Tickbird.Core.Query;
using Tickbird.Desktop.Services;
using ScottPlot.Avalonia;
using SpColor = ScottPlot.Color;
using SpCoordinates = ScottPlot.Coordinates;
using SpPixel = ScottPlot.Pixel;

namespace Tickbird.Desktop.Views.Requests;

/// <summary>How time-series lines are drawn.</summary>
public enum ChartStyle
{
    Line,
    Step,
    Points,
    Area
}

/// <summary>How a result is shown: table, chart or map.</summary>
public enum ResultMode
{
    Grid,
    Chart,
    Map
}

/// <summary>
/// Chart preferences shared by every result tab of one query tab, so they survive re-running the query.
/// <see cref="Version"/> changes on every edit; views compare it to know when to redraw.
/// </summary>
public sealed class ChartSettings
{
    private ResultMode mode;
    private ChartStyle style = ChartStyle.Line;
    private string? splitBy;
    private bool combineResults = true;
    private string palette = ChartPalettes.Default;
    private float lineWidth = 1.5f;

    public int Version { get; private set; }

    /// <summary>Color scheme for lines that have no color of their own (see <see cref="ChartPalettes"/>).</summary>
    public string Palette { get => palette; set => Set(ref palette, ChartPalettes.Names.Contains(value) ? value : ChartPalettes.Default); }

    /// <summary>Line width in pixels for lines that have no width of their own.</summary>
    public float LineWidth { get => lineWidth; set => Set(ref lineWidth, Math.Clamp(value, 0.5f, 12f)); }

    /// <summary>Colors chosen for individual lines, keyed by legend label, as #RRGGBB.</summary>
    public IReadOnlyDictionary<string, string> LineColors => lineColors;

    /// <summary>Widths chosen for individual lines, keyed by legend label.</summary>
    public IReadOnlyDictionary<string, float> LineWidths => lineWidths;

    private readonly Dictionary<string, string> lineColors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> lineWidths = new(StringComparer.Ordinal);

    /// <summary>Sets one line's color (#RRGGBB), or null to go back to the palette color.</summary>
    public void SetLineColor(string label, string? hex)
    {
        if (hex == null ? lineColors.Remove(label) : !lineColors.TryGetValue(label, out var old) || old != hex)
        {
            if (hex != null) lineColors[label] = hex;
            Version++;
        }
    }

    /// <summary>Sets one line's width, or null to follow <see cref="LineWidth"/>.</summary>
    public void SetLineWidth(string label, float? width)
    {
        if (width == null ? lineWidths.Remove(label) : !lineWidths.TryGetValue(label, out var old) || old != width)
        {
            if (width != null) lineWidths[label] = Math.Clamp(width.Value, 0.5f, 12f);
            Version++;
        }
    }

    /// <summary>Removes every per-line color and width.</summary>
    public void ResetLineStyles()
    {
        if (lineColors.Count == 0 && lineWidths.Count == 0) return;
        lineColors.Clear();
        lineWidths.Clear();
        Version++;
    }

    /// <summary>Grid, chart or map: how this query's results open.</summary>
    public ResultMode Mode { get => mode; set => Set(ref mode, value); }

    /// <summary>True when results open as a chart instead of a grid.</summary>
    public bool ShowChart { get => mode == ResultMode.Chart; set => Mode = value ? ResultMode.Chart : ResultMode.Grid; }

    /// <summary>Map bindings (latitude, longitude, color column and colors) for this query's results.</summary>
    public MapSettings Map { get; } = new();

    /// <summary>Row filter of this query's results; the grid, chart and map all show only the rows that pass.</summary>
    public RowFilter Filter { get; } = new();

    /// <summary>Changes whenever <see cref="Filter"/> is edited.</summary>
    public int FilterVersion { get; private set; }

    /// <summary>Call after editing <see cref="Filter"/>: result tabs re-filter, charts and maps redraw.</summary>
    public void FilterChanged()
    {
        FilterVersion++;
        Version++;
    }

    public ChartStyle Style { get => style; set => Set(ref style, value); }

    public string? SplitBy { get => splitBy; set => Set(ref splitBy, value); }

    /// <summary>Draw all result sets of the query on one chart (for example one line per GROUP BY tag set).</summary>
    public bool CombineResults { get => combineResults; set => Set(ref combineResults, value); }

    public HashSet<string> HiddenFields { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void SetFieldVisible(string field, bool visible)
    {
        if (visible ? HiddenFields.Remove(field) : HiddenFields.Add(field)) Version++;
    }

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Version++;
    }
}

/// <summary>The color schemes offered in the chart's Lines menu (all from ScottPlot).</summary>
public static class ChartPalettes
{
    public const string Default = "Category 10";

    public static readonly string[] Names =
    {
        "Category 10", "Category 20", "Colorblind friendly", "Dark", "Nord", "Aurora", "One Half", "Tsitsulin", "Microcharts"
    };

    public static ScottPlot.IPalette Get(string? name) => name switch
    {
        "Category 20" => new ScottPlot.Palettes.Category20(),
        "Colorblind friendly" => new ScottPlot.Palettes.ColorblindFriendly(),
        "Dark" => new ScottPlot.Palettes.Dark(),
        "Nord" => new ScottPlot.Palettes.Nord(),
        "Aurora" => new ScottPlot.Palettes.Aurora(),
        "One Half" => new ScottPlot.Palettes.OneHalf(),
        "Tsitsulin" => new ScottPlot.Palettes.Tsitsulin(),
        "Microcharts" => new ScottPlot.Palettes.Microcharts(),
        _ => new ScottPlot.Palettes.Category10()
    };
}

/// <summary>Grafana-style chart of a query result: zoom/pan, hover readout, field picker, split by tag, PNG export.</summary>
public sealed class ChartView : UserControl
{
    private static readonly IBrush Accent = Brush.Parse("#1a65a7");
    private static readonly string[] StyleNames = { "Line", "Step", "Points", "Area" };

    private readonly ChartSettings settings;
    private readonly AvaPlot plot = new();
    private readonly ComboBox styleBox = new() { ItemsSource = StyleNames, SelectedIndex = 0, MinWidth = 96 };
    private readonly ComboBox splitBox = new() { MinWidth = 120 };
    private readonly TextBlock splitLabel = Label("Split by");
    private readonly CheckBox combineBox = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button fieldsButton = new() { Content = "Fields ▾", Padding = new Thickness(8, 3) };
    private readonly StackPanel fieldsPanel = new() { Spacing = 2, Margin = new Thickness(4) };
    private static readonly float[] Widths = { 0.5f, 1, 1.5f, 2, 3, 4, 6 };
    private readonly ComboBox widthBox = new() { ItemsSource = Widths.Select(w => w.ToString("0.#", CultureInfo.CurrentCulture) + " px").ToList(), MinWidth = 80 };
    private readonly TextBlock widthLabel = Label("Width");
    private readonly Button linesButton = new() { Content = "Lines ▾", Padding = new Thickness(8, 3) };
    private readonly StackPanel linesPanel = new() { Spacing = 4, Margin = new Thickness(6), MinWidth = 300 };
    private ScottPlot.IPalette palette = ChartPalettes.Get(null);
    private ChartAxisKind drawnKind = ChartAxisKind.None;
    private readonly TextBlock status = new() { FontSize = 12, Foreground = Accent, Margin = new Thickness(2, 6, 2, 0), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock emptyText = new()
    {
        FontSize = 13, Foreground = Accent, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(24), IsVisible = false
    };
    private readonly StackPanel hoverRows = new() { Spacing = 2 };
    private readonly Border hover;

    private ResultSeries? current;
    private IReadOnlyList<ResultSeries>? all;
    private ChartData? data;
    private ScottPlot.Plottables.VerticalLine? hoverLine;
    private int renderedVersion = -1;
    private int buildId;
    private bool binding;

    public ChartView(ChartSettings settings)
    {
        this.settings = settings;
        hover = new Border
        {
            Background = new SolidColorBrush(Avalonia.Media.Color.Parse("#F7FFFFFF")),
            BorderBrush = Brush.Parse("#c9d9e8"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            IsVisible = false,
            Child = hoverRows
        };

        styleBox.SelectionChanged += (_, _) =>
        {
            if (binding || styleBox.SelectedIndex < 0) return;
            settings.Style = (ChartStyle)styleBox.SelectedIndex;
            Restyle();
        };
        widthBox.SelectionChanged += (_, _) =>
        {
            if (binding || widthBox.SelectedIndex < 0) return;
            settings.LineWidth = Widths[widthBox.SelectedIndex];
            Restyle();
        };
        ToolTip.SetTip(widthBox, "Width of every line. Set a single line's width in Lines ▾.");
        ToolTip.SetTip(linesButton, "Color scheme, and the color and width of each line.");
        linesButton.Flyout = new Flyout { Content = new ScrollViewer { MaxHeight = 460, Content = linesPanel }, Placement = PlacementMode.BottomEdgeAlignedLeft };
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
        fieldsButton.Flyout = new Flyout { Content = new ScrollViewer { MaxHeight = 360, Content = fieldsPanel }, Placement = PlacementMode.BottomEdgeAlignedLeft };

        var reset = new Button { Content = "Reset zoom", Padding = new Thickness(8, 3) };
        ToolTip.SetTip(reset, "Fit all data (or double-click the chart). Scroll to zoom, drag to pan, middle-drag to zoom into an area, right-drag to stretch an axis, right-click for more.");
        reset.Click += (_, _) => { plot.Plot.Axes.AutoScale(); plot.Refresh(); };
        var save = new Button { Content = "Save PNG…", Padding = new Thickness(8, 3) };
        save.Click += async (_, _) => await SavePngAsync();

        var toolbar = new ResponsiveToolBar();
        toolbar.Add(Label("Style"), styleBox);
        toolbar.Add(widthLabel, widthBox);
        toolbar.Add(linesButton);
        toolbar.Add(splitLabel, splitBox);
        toolbar.Add(fieldsButton);
        toolbar.Add(combineBox);
        toolbar.Add(reset);
        toolbar.Add(save);

        // Double-click fits the data (ScottPlot's default shows a debug benchmark overlay instead).
        var input = plot.UserInputProcessor;
        input.RemoveAll<ScottPlot.Interactivity.UserActionResponses.DoubleClickBenchmark>();
        input.UserActionResponses.Add(new ScottPlot.Interactivity.UserActionResponses.DoubleClickResponse(
            ScottPlot.Interactivity.StandardMouseButtons.Left, ScottPlot.Interactivity.UserActionResponses.SingleClickAutoscale.AutoScale));
        plot.PointerMoved += OnPlotPointerMoved;
        plot.PointerExited += (_, _) => HideHover();

        var surface = new Grid();
        surface.Children.Add(plot);
        surface.Children.Add(emptyText);
        surface.Children.Add(hover);

        var layout = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(status, Dock.Bottom);
        layout.Children.Add(toolbar);
        layout.Children.Add(status);
        layout.Children.Add(new Border
        {
            Background = Brushes.White,
            BorderBrush = Brush.Parse("#e5eef5"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            ClipToBounds = true,
            // Let the surface shrink inside a short query result pane without covering its toolbar.
            Child = surface
        });
        Content = layout;
    }

    /// <summary>The plot control (exposed for tests).</summary>
    public AvaPlot Plot => plot;

    /// <summary>The data currently drawn (exposed for tests).</summary>
    public ChartData? Data => data;

    /// <summary>Sets the result to chart. <paramref name="allResults"/> are the query's other result sets, for "combine".</summary>
    public void SetResults(ResultSeries result, IReadOnlyList<ResultSeries>? allResults)
    {
        current = result;
        all = allResults;
        renderedVersion = -1;
    }

    /// <summary>Redraws when settings changed in another tab since this chart was drawn.</summary>
    public Task EnsureCurrentAsync() => renderedVersion == settings.Version ? Task.CompletedTask : RefreshAsync();

    /// <summary>Rebuilds chart data (off the UI thread) and redraws.</summary>
    public async Task RefreshAsync()
    {
        if (current == null) return;
        var id = ++buildId;
        var sources = Sources();
        var options = new ChartOptions
        {
            SplitBy = settings.SplitBy,
            HiddenFields = new HashSet<string>(settings.HiddenFields, StringComparer.OrdinalIgnoreCase)
        };
        status.Text = "Preparing chart…";
        ChartData built;
        try
        {
            built = await Task.Run(() => ChartDataBuilder.Build(sources, options));
        }
        catch (Exception ex)
        {
            built = ChartData.Empty("Could not chart this result: " + ex.Message);
        }
        if (id != buildId) return;                       // a newer refresh started meanwhile
        data = built;
        renderedVersion = settings.Version;
        BindToolbar(built, sources.Count);
        Redraw();
    }

    private IReadOnlyList<ResultSeries> Sources() =>
        settings.CombineResults && all is { Count: > 1 } ? all : new[] { current! };

    private void BindToolbar(ChartData chart, int sourceCount)
    {
        binding = true;
        try
        {
            styleBox.SelectedIndex = (int)settings.Style;
            styleBox.IsEnabled = chart.AxisKind != ChartAxisKind.Category;
            widthBox.SelectedIndex = NearestWidthIndex(settings.LineWidth);
            widthLabel.IsVisible = widthBox.IsVisible = chart.AxisKind == ChartAxisKind.Time;
            linesButton.Content = chart.AxisKind == ChartAxisKind.Category ? "Colors ▾" : "Lines ▾";
            linesButton.IsVisible = chart.Series.Count > 0;
            BuildLinesPanel(chart);
            ToolTip.SetTip(styleBox, chart.AxisKind == ChartAxisKind.Category ? "Results without a time column are drawn as bars." : null);

            var splits = new List<string> { "(none)" };
            splits.AddRange(chart.SplitCandidates);
            splitBox.ItemsSource = splits;
            splitBox.SelectedIndex = settings.SplitBy != null
                ? Math.Max(0, splits.FindIndex(s => string.Equals(s, settings.SplitBy, StringComparison.OrdinalIgnoreCase)))
                : 0;
            var canSplit = chart.SplitCandidates.Count > 0 && chart.AxisKind != ChartAxisKind.Category;
            splitLabel.IsVisible = splitBox.IsVisible = canSplit;
            ToolTip.SetTip(splitBox, "Draw one line per value of a text (tag) column.");

            var total = all?.Count ?? 1;
            combineBox.IsVisible = total > 1;
            combineBox.Content = $"Combine all {total} results";
            combineBox.IsChecked = settings.CombineResults;

            fieldsPanel.Children.Clear();
            foreach (var field in chart.Fields)
            {
                var check = new CheckBox { Content = field, IsChecked = !settings.HiddenFields.Contains(field) };
                check.IsCheckedChanged += (_, _) =>
                {
                    if (binding) return;
                    settings.SetFieldVisible(field, check.IsChecked == true);
                    _ = RefreshAsync();
                };
                fieldsPanel.Children.Add(check);
            }
            fieldsButton.IsVisible = chart.Fields.Count > 1;
            var hidden = chart.Fields.Count(f => settings.HiddenFields.Contains(f));
            fieldsButton.Content = hidden > 0 ? $"Fields ({chart.Fields.Count - hidden}/{chart.Fields.Count}) ▾" : "Fields ▾";
        }
        finally
        {
            binding = false;
        }
    }

    /// <summary>Applies a styling change (color, width, style) without rebuilding data or losing the zoom.</summary>
    private void Restyle()
    {
        renderedVersion = settings.Version;
        Redraw(keepView: true);
    }

    private void Redraw(bool keepView = false)
    {
        HideHover();
        ScottPlot.AxisLimits? view = keepView && drawnKind != ChartAxisKind.None && drawnKind == data?.AxisKind
            ? plot.Plot.Axes.GetLimits()
            : null;
        plot.Reset();
        palette = ChartPalettes.Get(settings.Palette);
        hoverLine = null;
        var p = plot.Plot;
        p.FigureBackground.Color = SpColor.FromHex("#FFFFFF");
        p.DataBackground.Color = SpColor.FromHex("#FFFFFF");
        p.Axes.Color(SpColor.FromHex("#4A6A88"));
        p.Grid.MajorLineColor = SpColor.FromHex("#EAF1F7");

        var chart = data;
        var drawable = chart != null && chart.AxisKind != ChartAxisKind.None && chart.Series.Count > 0;
        emptyText.IsVisible = !drawable;
        plot.IsVisible = drawable;
        drawnKind = drawable ? chart!.AxisKind : ChartAxisKind.None;
        if (!drawable)
        {
            emptyText.Text = chart?.Message ?? "Nothing to chart.";
            status.Text = chart?.SkippedColumns.Count > 0 ? "Not plottable: " + string.Join(", ", chart.SkippedColumns) : "";
            plot.Refresh();
            return;
        }

        if (chart!.AxisKind == ChartAxisKind.Time) DrawTime(p, chart);
        else DrawBars(p, chart);

        if (chart.Series.Count > 1) p.ShowLegend(ScottPlot.Edge.Right);
        else p.YLabel(chart.Series[0].Label, 12);
        p.Axes.AutoScale();
        if (view is { } limits) p.Axes.SetLimits(limits);
        status.Text = Summary(chart);
        plot.Refresh();
    }

    private void DrawTime(ScottPlot.Plot p, ChartData chart)
    {
        p.Axes.DateTimeTicksBottom();
        p.XLabel($"{chart.XColumn} (UTC)", 12);
        var style = settings.Style;
        for (var i = 0; i < chart.Series.Count; i++)
        {
            var line = chart.Series[i];
            var width = WidthFor(line);
            var scatter = p.Add.Scatter(line.Xs, line.Ys, ColorFor(line, i));
            scatter.LegendText = line.Label;
            scatter.LineWidth = style == ChartStyle.Points ? 0 : width;
            scatter.MarkerSize = style == ChartStyle.Points ? Math.Max(4, width * 2.5f) : line.Xs.Length <= 60 ? width + 2.5f : 0;
            if (style == ChartStyle.Step) scatter.ConnectStyle = ScottPlot.ConnectStyle.StepHorizontal;
            if (style == ChartStyle.Area)
            {
                scatter.FillY = true;
                scatter.FillYValue = 0;
                scatter.FillYColor = scatter.Color.WithAlpha(.15);
            }
        }
        hoverLine = p.Add.VerticalLine(chart.Series[0].Xs.FirstOrDefault(), 1, SpColor.FromHex("#7A93AB"), ScottPlot.LinePattern.Dashed);
        hoverLine.IsVisible = false;
    }

    private void DrawBars(ScottPlot.Plot p, ChartData chart)
    {
        var groups = chart.Series.Count;
        var width = 0.8 / groups;
        for (var g = 0; g < groups; g++)
        {
            var line = chart.Series[g];
            var positions = new List<double>();
            var values = new List<double>();
            for (var i = 0; i < line.Xs.Length; i++)
            {
                if (double.IsNaN(line.Ys[i])) continue;
                positions.Add(line.Xs[i] - 0.4 + width * (g + 0.5));
                values.Add(line.Ys[i]);
            }
            var bars = p.Add.Bars(positions, values);
            bars.LegendText = line.Label;
            bars.Color = ColorFor(line, g);
            foreach (var bar in bars.Bars)
            {
                bar.Size = width * 0.95;
                bar.LineWidth = 0;
            }
        }
        var ticks = Enumerable.Range(0, chart.Categories.Count).Select(i => (double)i).ToArray();
        p.Axes.Bottom.SetTicks(ticks, chart.Categories.Select(c => c.Length > 28 ? c[..27] + "…" : c).ToArray());
        if (chart.Categories.Count > 8)
        {
            p.Axes.Bottom.TickLabelStyle.Rotation = 45;
            p.Axes.Bottom.TickLabelStyle.Alignment = ScottPlot.Alignment.MiddleLeft;
        }
        if (chart.XColumn != null) p.XLabel(chart.XColumn, 12);
        p.Axes.Margins(bottom: 0);
    }

    /// <summary>The line's own color if one was picked, otherwise the palette color for its position.</summary>
    private SpColor ColorFor(ChartSeries line, int index) =>
        settings.LineColors.TryGetValue(line.Label, out var hex) && TryParseColor(hex, out var color) ? color : palette.GetColor(index);

    private float WidthFor(ChartSeries line) =>
        settings.LineWidths.TryGetValue(line.Label, out var width) ? width : settings.LineWidth;

    private static int NearestWidthIndex(float width)
    {
        var best = 0;
        for (var i = 1; i < Widths.Length; i++)
            if (Math.Abs(Widths[i] - width) < Math.Abs(Widths[best] - width)) best = i;
        return best;
    }

    /// <summary>Parses #RGB, #RRGGBB, #AARRGGBB or a color name such as "orange".</summary>
    public static bool TryParseColor(string? text, out SpColor color)
    {
        color = default;
        var value = text?.Trim() ?? "";
        if (value.Length is 3 or 6 or 8 && value.All(Uri.IsHexDigit)) value = "#" + value;
        if (!Avalonia.Media.Color.TryParse(value, out var parsed)) return false;
        color = new SpColor(parsed.R, parsed.G, parsed.B, parsed.A);
        return true;
    }

    public static string ToHex(SpColor color) => color.A == 255
        ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
        : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private static IBrush BrushOf(SpColor color) => new SolidColorBrush(Avalonia.Media.Color.FromArgb(color.A, color.R, color.G, color.B));

    // Distinct, readable colors offered next to the palette's own.
    private static readonly string[] PresetColors =
    {
        "#000000", "#4D4D4D", "#999999", "#0B3D91", "#1F77B4", "#00A3E0", "#17BECF", "#2CA02C",
        "#8BC34A", "#F2C200", "#FF7F0E", "#D62728", "#C2185B", "#9467BD", "#8C564B", "#E377C2"
    };

    /// <summary>Lines ▾ menu: palette, then one row per line with a color swatch (opens a picker) and a width.</summary>
    private void BuildLinesPanel(ChartData chart)
    {
        linesPanel.Children.Clear();
        palette = ChartPalettes.Get(settings.Palette);

        var paletteBox = new ComboBox { ItemsSource = ChartPalettes.Names, SelectedItem = settings.Palette, MinWidth = 170 };
        paletteBox.SelectionChanged += (_, _) =>
        {
            if (binding || paletteBox.SelectedItem is not string name) return;
            settings.Palette = name;
            Restyle();
            if (data != null) BuildLinesPanel(data);      // swatches of palette-colored lines change
        };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 4) };
        top.Children.Add(Label("Color scheme"));
        top.Children.Add(paletteBox);
        linesPanel.Children.Add(top);

        var showWidth = chart.AxisKind == ChartAxisKind.Time;
        for (var i = 0; i < chart.Series.Count; i++)
            linesPanel.Children.Add(LineRow(chart.Series[i], i, showWidth));

        var reset = new Button { Content = "Reset all lines", Padding = new Thickness(8, 3), Margin = new Thickness(0, 6, 0, 0) };
        ToolTip.SetTip(reset, "Go back to the color scheme and the toolbar width for every line.");
        reset.Click += (_, _) =>
        {
            settings.ResetLineStyles();
            Restyle();
            if (data != null) BuildLinesPanel(data);
        };
        linesPanel.Children.Add(reset);
    }

    private Control LineRow(ChartSeries line, int index, bool showWidth)
    {
        var swatch = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(3), Background = BrushOf(ColorFor(line, index)), BorderBrush = Brush.Parse("#9fb3c6"), BorderThickness = new Thickness(1) };
        var swatchButton = new Button { Content = swatch, Padding = new Thickness(3), Background = Brushes.Transparent };
        ToolTip.SetTip(swatchButton, "Pick a color for this line");
        var name = new TextBlock { Text = line.Label, FontSize = 12, Foreground = Brush.Parse("#33536F"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260 };
        if (settings.LineColors.ContainsKey(line.Label) || settings.LineWidths.ContainsKey(line.Label)) name.FontWeight = FontWeight.SemiBold;

        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(swatchButton, Dock.Left);
        row.Children.Add(swatchButton);
        if (showWidth)
        {
            var widths = new List<string> { "Toolbar width" };
            widths.AddRange(Widths.Select(w => w.ToString("0.#", CultureInfo.CurrentCulture) + " px"));
            var widthBox = new ComboBox { ItemsSource = widths, MinWidth = 120, Margin = new Thickness(8, 0, 0, 0), FontSize = 12 };
            widthBox.SelectedIndex = settings.LineWidths.TryGetValue(line.Label, out var own) ? NearestWidthIndex(own) + 1 : 0;
            widthBox.SelectionChanged += (_, _) =>
            {
                if (binding || widthBox.SelectedIndex < 0) return;
                settings.SetLineWidth(line.Label, widthBox.SelectedIndex == 0 ? null : Widths[widthBox.SelectedIndex - 1]);
                Restyle();
            };
            DockPanel.SetDock(widthBox, Dock.Right);
            row.Children.Add(widthBox);
        }
        row.Children.Add(name);

        // The picker opens inside the menu (a second pop-up would close the first).
        var picker = new StackPanel { Spacing = 4, IsVisible = false, Margin = new Thickness(30, 2, 0, 6) };
        void Apply(string? hex)
        {
            settings.SetLineColor(line.Label, hex);
            swatch.Background = BrushOf(ColorFor(line, index));
            name.FontWeight = settings.LineColors.ContainsKey(line.Label) || settings.LineWidths.ContainsKey(line.Label) ? FontWeight.SemiBold : FontWeight.Normal;
            Restyle();
        }
        var swatches = new WrapPanel { MaxWidth = 300 };
        foreach (var color in palette.Colors.Take(10).Select(ToHex).Concat(PresetColors).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var hex = color;
            var choice = new Button
            {
                Width = 22, Height = 22, Padding = new Thickness(0), Margin = new Thickness(0, 0, 3, 3),
                Background = BrushOf(TryParseColor(hex, out var c) ? c : default), BorderBrush = Brush.Parse("#c9d9e8"), BorderThickness = new Thickness(1)
            };
            ToolTip.SetTip(choice, hex);
            choice.Click += (_, _) => Apply(hex);
            swatches.Children.Add(choice);
        }
        picker.Children.Add(swatches);

        var hexBox = new TextBox { Watermark = "#RRGGBB", Width = 110, FontSize = 12, Text = settings.LineColors.TryGetValue(line.Label, out var current) ? current : "" };
        ToolTip.SetTip(hexBox, "A hex color such as #FF8800, or a name such as orange");
        var applyHex = new Button { Content = "Apply", Padding = new Thickness(8, 2), FontSize = 12 };
        var auto = new Button { Content = "Automatic", Padding = new Thickness(8, 2), FontSize = 12 };
        ToolTip.SetTip(auto, "Use the color scheme's color for this line");
        void ApplyText()
        {
            if (TryParseColor(hexBox.Text, out var parsed)) { hexBox.Text = ToHex(parsed); Apply(ToHex(parsed)); }
            else hexBox.Text = "";
        }
        applyHex.Click += (_, _) => ApplyText();
        hexBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ApplyText(); e.Handled = true; } };
        auto.Click += (_, _) => { hexBox.Text = ""; Apply(null); };
        var custom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        custom.Children.Add(hexBox);
        custom.Children.Add(applyHex);
        custom.Children.Add(auto);
        picker.Children.Add(custom);

        swatchButton.Click += (_, _) => picker.IsVisible = !picker.IsVisible;

        var block = new StackPanel { Spacing = 2 };
        block.Children.Add(row);
        block.Children.Add(picker);
        return block;
    }

    private static string Summary(ChartData chart)
    {
        var culture = CultureInfo.CurrentCulture;
        var text = chart.AxisKind == ChartAxisKind.Category
            ? $"{chart.Series.Count} field(s) · {chart.Categories.Count.ToString("N0", culture)} bars"
            : $"{chart.Series.Count} line(s) · {chart.TotalPoints.ToString("N0", culture)} points";
        if (chart.IsDownsampled)
            text += $" (reduced from {chart.SourcePoints.ToString("N0", culture)} for drawing; the shape is kept)";
        if (chart.SkippedColumns.Count > 0) text += " · not plottable: " + string.Join(", ", chart.SkippedColumns);
        if (chart.Message != null) text += " · " + chart.Message;
        return text;
    }

    private void OnPlotPointerMoved(object? sender, PointerEventArgs e)
    {
        var chart = data;
        if (chart == null || chart.Series.Count == 0 || chart.AxisKind == ChartAxisKind.None) return;
        var position = e.GetPosition(plot);
        var p = plot.Plot;
        var mouse = p.GetCoordinates(new SpPixel((float)position.X, (float)position.Y));
        var limits = p.Axes.GetLimits();
        if (mouse.X < limits.Left || mouse.X > limits.Right || mouse.Y < limits.Bottom || mouse.Y > limits.Top)
        {
            HideHover();
            return;
        }

        var rows = new List<(string Label, double Value, SpColor Color)>();
        string heading;
        if (chart.AxisKind == ChartAxisKind.Category)
        {
            var index = (int)Math.Round(mouse.X);
            if (index < 0 || index >= chart.Categories.Count) { HideHover(); return; }
            heading = chart.Categories[index];
            for (var s = 0; s < chart.Series.Count; s++)
                rows.Add((chart.Series[s].Label, chart.Series[s].Ys[index], ColorFor(chart.Series[s], s)));
        }
        else
        {
            // Snap to the data point nearest the cursor in time, then show every line's value at that time.
            double? snapX = null;
            var bestPixels = double.MaxValue;
            foreach (var line in chart.Series)
            {
                var i = NearestIndex(line, mouse.X);
                if (i < 0) continue;
                var distance = Math.Abs(p.GetPixel(new SpCoordinates(line.Xs[i], 0)).X - position.X);
                if (distance < bestPixels) { bestPixels = distance; snapX = line.Xs[i]; }
            }
            if (snapX == null || bestPixels > 40) { HideHover(); return; }
            heading = FormatTime(ChartValues.FromX(snapX.Value));
            for (var s = 0; s < chart.Series.Count; s++)
            {
                var line = chart.Series[s];
                var i = NearestIndex(line, snapX.Value);
                if (i < 0 || Math.Abs(p.GetPixel(new SpCoordinates(line.Xs[i], 0)).X - p.GetPixel(new SpCoordinates(snapX.Value, 0)).X) > 2) continue;
                rows.Add((line.Label, line.Ys[i], ColorFor(line, s)));
            }
            if (hoverLine != null)
            {
                hoverLine.X = snapX.Value;
                hoverLine.IsVisible = true;
                plot.Refresh();
            }
        }

        ShowHover(heading, rows, position);
    }

    /// <summary>Index of the non-NaN point closest to <paramref name="x"/> (Xs are sorted), or -1.</summary>
    private static int NearestIndex(ChartSeries line, double x)
    {
        var xs = line.Xs;
        if (xs.Length == 0) return -1;
        var i = Array.BinarySearch(xs, x);
        if (i < 0) i = ~i;
        int best = -1;
        var bestDistance = double.MaxValue;
        // Look a little either side so a null at the exact spot falls back to a neighbour.
        for (var j = Math.Max(0, i - 3); j < Math.Min(xs.Length, i + 3); j++)
        {
            if (double.IsNaN(line.Ys[j])) continue;
            var d = Math.Abs(xs[j] - x);
            if (d < bestDistance) { bestDistance = d; best = j; }
        }
        return best;
    }

    private void ShowHover(string heading, List<(string Label, double Value, SpColor Color)> rows, Point position)
    {
        hoverRows.Children.Clear();
        hoverRows.Children.Add(new TextBlock { Text = heading, FontWeight = FontWeight.SemiBold, FontSize = 12, Foreground = Accent, Margin = new Thickness(0, 0, 0, 2) });
        const int maxRows = 15;
        foreach (var row in rows.Take(maxRows))
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            line.Children.Add(new Border
            {
                Width = 10, Height = 10, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Avalonia.Media.Color.FromArgb(row.Color.A, row.Color.R, row.Color.G, row.Color.B))
            });
            line.Children.Add(new TextBlock { Text = row.Label, FontSize = 12, Foreground = Brush.Parse("#33536F") });
            line.Children.Add(new TextBlock { Text = ChartValues.FormatNumber(row.Value), FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#10314F") });
            hoverRows.Children.Add(line);
        }
        if (rows.Count > maxRows)
            hoverRows.Children.Add(new TextBlock { Text = $"+{rows.Count - maxRows} more", FontSize = 11, Foreground = Accent });

        hover.IsVisible = true;
        // Keep the box inside the chart: flip to the left/top of the cursor near the edges.
        var width = hover.Bounds.Width > 0 ? hover.Bounds.Width : 220;
        var height = hover.Bounds.Height > 0 ? hover.Bounds.Height : 24 + rows.Count * 18;
        var x = position.X + 16 + width > plot.Bounds.Width ? Math.Max(0, position.X - 16 - width) : position.X + 16;
        var y = position.Y + 16 + height > plot.Bounds.Height ? Math.Max(0, position.Y - 16 - height) : position.Y + 16;
        hover.Margin = new Thickness(x, y, 0, 0);
    }

    private void HideHover()
    {
        hover.IsVisible = false;
        if (hoverLine is { IsVisible: true })
        {
            hoverLine.IsVisible = false;
            plot.Refresh();
        }
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

    private async Task SavePngAsync()
    {
        if (data == null || data.Series.Count == 0) return;
        var name = ResultExporter.SafeFileName($"{current?.Name ?? "query"}_chart.png");
        var path = await FilePicker.SaveAsync(this, name, "PNG images", "*.png");
        if (path == null) return;
        try
        {
            HideHover();
            var width = Math.Max(1200, (int)plot.Bounds.Width);
            var height = Math.Max(675, (int)plot.Bounds.Height);
            plot.Plot.SavePng(path, width, height);
        }
        catch (Exception ex)
        {
            if (TopLevel.GetTopLevel(this) is Window w) await Dialogs.ShowExceptionAsync(w, ex);
        }
    }

    private static TextBlock Label(string text) => new() { Text = text, Foreground = Accent, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
}
