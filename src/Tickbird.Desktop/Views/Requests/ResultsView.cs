using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Styling;
using Tickbird.Core.Data;
using Tickbird.Core.Mapping;
using Tickbird.Core.Query;
using Tickbird.Desktop.Services;

namespace Tickbird.Desktop.Views.Requests;

/// <summary>One query result set: tags line plus a grid (copy, export) or a chart, switched with the Grid/Chart buttons.</summary>
public sealed class ResultsView : UserControl
{
    /// <summary>Row proxy: cells are formatted on demand from the underlying result.</summary>
    private sealed class ResultRow
    {
        private readonly ResultSeries series;
        private readonly ResultFormatter formatter;
        private readonly int number;
        private readonly ISet<string> timeColumns;
        public ResultRow(int index, int number, ResultSeries series, ResultFormatter formatter, ISet<string> timeColumns)
        { Index = index; this.number = number; this.series = series; this.formatter = formatter; this.timeColumns = timeColumns; }
        /// <summary>Row in the shown (filtered) result.</summary>
        public int Index { get; }

        public string? this[int column]
        {
            get
            {
                // The # column keeps the row's number in the full result, so filtered rows can be found again.
                if (column == 0) return number.ToString(CultureInfo.CurrentCulture);
                var c = column - 1;
                var row = series.Values[Index];
                return c < series.Columns.Count && c < row.Count ? formatter.Format(series.Columns[c], row[c], null, timeColumns.Contains(series.Columns[c])) : null;
            }
        }
    }

    private readonly DataGrid grid = GridFactory.Create();
    /// <summary>Columns the user chose to show as date/time (epoch values), by name.</summary>
    private readonly HashSet<string> timeColumns = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBlock tags = new()
    {
        Margin = new Thickness(10, 0), IsVisible = false, TextTrimming = TextTrimming.CharacterEllipsis,
        FontSize = 12, Foreground = Brush.Parse("#1a65a7")
    };
    private readonly ToggleButton gridButton = ModeButton("Grid", "Show the result as a table");
    private readonly ToggleButton chartButton = ModeButton("Chart", "Plot the numeric columns over time (or as bars)");
    private readonly ToggleButton mapButton = ModeButton("Map", "Draw latitude/longitude columns as lines on a map");
    private readonly Border gridCard;
    private readonly Panel body = new Grid();
    private ChartView? chart;
    private MapView? map;
    private ResultSeries? last;
    private ResultSeries? shown;
    private IReadOnlyList<ResultSeries>? shownAll;
    private IReadOnlyList<int> shownNumbers = Array.Empty<int>();
    private int appliedFilterVersion = -1;
    private readonly TextBox quickFilter = new() { Watermark = "Filter rows…", Width = 220, FontSize = 12, Padding = new Thickness(8, 3), MinHeight = 26 };
    private readonly Button conditionsButton = new() { Content = "Conditions ▾", Padding = new Thickness(8, 2), MinHeight = 26, FontSize = 12 };
    private readonly StackPanel conditionsPanel = new() { Spacing = 4, Margin = new Thickness(6), MinWidth = 420 };
    private readonly Button clearFilter = new() { Content = "Clear filter", Padding = new Thickness(8, 2), MinHeight = 26, FontSize = 12, IsVisible = false };
    private readonly TextBlock filterStatus = new() { FontSize = 12, Foreground = Brush.Parse("#1a65a7"), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
    private readonly Avalonia.Threading.DispatcherTimer quickFilterDelay = new() { Interval = TimeSpan.FromMilliseconds(300) };

    public ResultsView()
    {
        grid.Classes.Add("queryResults");
        grid.Background = Brushes.White;
        grid.Foreground = Brush.Parse("#1a65a7");
        grid.FontSize = 13;
        grid.RowHeight = 28;
        grid.ColumnHeaderHeight = 34;
        // Pixel widths sized to the content (set in Show), not star widths: star columns always share the grid's
        // width, so dragging one edge only steals from its neighbours. Wider columns scroll horizontally instead.
        grid.CanUserResizeColumns = true;
        grid.FrozenColumnCount = 1;   // the # column stays in view while scrolling sideways
        grid.HorizontalGridLinesBrush = Brush.Parse("#F0F6FC");
        grid.VerticalGridLinesBrush = Brush.Parse("#e6eef5");
        grid.BorderThickness = new Thickness(0);
        grid.SelectionMode = DataGridSelectionMode.Extended;
        grid.ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader;
        grid.ContextMenu = new ContextMenu
        {
            Items =
            {
                Item("Copy Cell", CopyCell),
                Item("Copy Row", () => CopyRows(CurrentRow().ToArray())),
                Item("Copy Selected Rows", () => CopyRows(SelectedIndices().OrderBy(i => i).ToArray())),
                new Separator(),
                Item("Filter to This Value", () => FilterByCell(MapConditionOperator.Equals)),
                Item("Exclude This Value", () => FilterByCell(MapConditionOperator.NotEquals)),
                Item("Clear Filter", ClearFilter),
                new Separator(),
                Item("Show Column as Date/Time (toggle)", ToggleTimeColumn),
                Item("Auto-size Columns", AutoSizeColumns),
                Item("Export All to CSV…", () => Export(false, true)),
                Item("Export All to JSON…", () => Export(false, false)),
                Item("Export Selected to CSV…", () => Export(true, true)),
                Item("Export Selected to JSON…", () => Export(true, false)),
                new Separator(),
                Item("Show as Chart", () => SetChartMode(true)),
                Item("Show on Map", () => SetMode(ResultMode.Map)),
            }
        };
        gridButton.Click += (_, _) => SetMode(ResultMode.Grid);
        chartButton.Click += (_, _) => SetMode(ResultMode.Chart);
        mapButton.Click += (_, _) => SetMode(ResultMode.Map);
        AddModeButtonStyles(Styles);
        // Grid | Chart as one joined (segmented) control in the app's blue palette.
        var segments = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 0 };
        segments.Children.Add(gridButton);
        segments.Children.Add(new Border { Width = 1, Background = Brush.Parse("#c9d9e8") });
        segments.Children.Add(chartButton);
        segments.Children.Add(new Border { Width = 1, Background = Brush.Parse("#c9d9e8") });
        segments.Children.Add(mapButton);
        var modes = new Border
        {
            BorderBrush = Brush.Parse("#c9d9e8"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Child = segments
        };

        // One row: filter box and Conditions on the left, tags in the middle, Grid | Chart | Map on the right.
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(modes, Dock.Right);
        header.Children.Add(modes);
        var filterBar = BuildFilterBar();
        DockPanel.SetDock(filterBar, Dock.Left);
        header.Children.Add(filterBar);
        header.Children.Add(tags);
        tags.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;

        gridCard = new Border
        {
            Background = Brushes.White,
            BorderBrush = Brush.Parse("#e5eef5"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            ClipToBounds = true,
            Child = grid
        };
        body.Children.Add(gridCard);

        var panel = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        panel.Children.Add(body);
        Content = panel;
        UpdateModeButtons();
        // Chart settings are shared by the query's result tabs: re-apply them when this tab is shown again.
        AttachedToVisualTree += (_, _) => ApplySettings();
    }

    public DatabaseClient? Client { get; set; }
    public string? Database { get; set; }

    public int RowCount => last?.Values.Count ?? 0;

    /// <summary>Rows that pass the filter (all rows when nothing is filtered).</summary>
    public int ShownRowCount => shown?.Values.Count ?? 0;

    /// <summary>The filtered result shown in the grid, chart and map (exposed for tests).</summary>
    public ResultSeries? Shown => shown;

    /// <summary>Chart preferences; a query tab shares one instance across its result tabs and re-runs.</summary>
    public ChartSettings ChartSettings { get; set; } = new();

    /// <summary>All result sets of the query this one belongs to, so a chart can combine them (GROUP BY tag).</summary>
    public IReadOnlyList<ResultSeries>? AllResults { get; set; }

    /// <summary>True when the chart is showing instead of the grid.</summary>
    public bool IsChartMode => chart is { IsVisible: true };

    /// <summary>True when the map is showing.</summary>
    public bool IsMapMode => map is { IsVisible: true };

    /// <summary>The chart, once it has been opened (exposed for tests).</summary>
    public ChartView? Chart => chart;

    /// <summary>The map, once it has been opened (exposed for tests).</summary>
    public MapView? Map => map;

    /// <summary>Switches between grid and chart and remembers the choice for the query tab.</summary>
    public void SetChartMode(bool showChart) => SetMode(showChart ? ResultMode.Chart : ResultMode.Grid);

    /// <summary>Shows the grid, chart or map, and remembers the choice for the query tab.</summary>
    public void SetMode(ResultMode mode)
    {
        ChartSettings.Mode = mode;
        ApplyMode();
    }

    public int Show(ResultSeries result)
    {
        last = result;
        appliedFilterVersion = -1;
        tags.IsVisible = result.Tags.Count > 0;
        tags.Text = string.Join(", ", result.Tags.Select(t => $"{t.Key} = {t.Value}"));
        grid.ItemsSource = null;
        grid.Columns.Clear();
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "#", Binding = new Binding("[0]"), IsReadOnly = true, CanUserResize = true, MinWidth = 40,
            Width = new DataGridLength(RowNumberWidth(result.Values.Count))
        });
        for (var i = 0; i < result.Columns.Count; i++)
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = result.Columns[i].Replace("_", "__"), Binding = new Binding($"[{i + 1}]"), IsReadOnly = true,
                CanUserResize = true, MinWidth = 40, Width = new DataGridLength(FitWidth(result, i, TickbirdApp.Current.Formatter))
            });
        ApplyFilter();
        ApplyMode();
        return result.Values.Count;
    }

    public void Clear()
    {
        last = null;
        shown = null;
        shownAll = null;
        grid.ItemsSource = null;
        grid.Columns.Clear();
        tags.IsVisible = false;
        if (chart != null)
        {
            body.Children.Remove(chart);
            chart = null;
        }
        if (map != null)
        {
            body.Children.Remove(map);
            map = null;
        }
        ApplyMode();
    }

    private void ApplySettings()
    {
        // The filter is shared by the query's result tabs: another tab may have changed it.
        if (last != null && appliedFilterVersion != ChartSettings.FilterVersion)
        {
            ApplyFilter();
            ApplyMode();
            return;
        }
        if (IsChartMode != (CurrentMode == ResultMode.Chart) || IsMapMode != (CurrentMode == ResultMode.Map)) ApplyMode();
        else if (chart is { IsVisible: true }) _ = chart.EnsureCurrentAsync();
        else if (map is { IsVisible: true }) _ = map.EnsureCurrentAsync();
    }

    /// <summary>The mode to show: the query tab's choice, or the grid while there is no result.</summary>
    private ResultMode CurrentMode => last == null ? ResultMode.Grid : ChartSettings.Mode;

    private void ApplyMode()
    {
        var mode = CurrentMode;
        // Chart and map are built on first use so plain grid users pay nothing for them.
        if (mode == ResultMode.Chart && chart == null)
        {
            chart = new ChartView(ChartSettings);
            chart.SetResults(shown ?? last!, shownAll ?? AllResults);
            body.Children.Add(chart);
        }
        if (mode == ResultMode.Map && map == null)
        {
            map = new MapView(ChartSettings.Map);
            map.SetResults(shown ?? last!, shownAll ?? AllResults);
            body.Children.Add(map);
        }
        gridCard.IsVisible = mode == ResultMode.Grid;
        if (chart != null) chart.IsVisible = mode == ResultMode.Chart;
        if (map != null) map.IsVisible = mode == ResultMode.Map;
        UpdateModeButtons();
        if (mode == ResultMode.Chart) _ = chart!.EnsureCurrentAsync();
        if (mode == ResultMode.Map) _ = map!.EnsureCurrentAsync();
    }

    private void UpdateModeButtons()
    {
        var mode = CurrentMode;
        gridButton.IsChecked = mode == ResultMode.Grid;
        chartButton.IsChecked = mode == ResultMode.Chart;
        mapButton.IsChecked = mode == ResultMode.Map;
        chartButton.IsEnabled = mapButton.IsEnabled = last != null;
    }

    private static ToggleButton ModeButton(string text, string tip)
    {
        var button = new ToggleButton
        {
            Content = text, Padding = new Thickness(12, 0), Height = 26, MinHeight = 26, FontSize = 12, MinWidth = 56,
            CornerRadius = new CornerRadius(0), BorderThickness = new Thickness(0),
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        button.Classes.Add("resultMode");
        ToolTip.SetTip(button, tip);
        return button;
    }

    /// <summary>Grid/Chart colours: white with blue text, light blue when selected (as in the tree and toolbar).</summary>
    private static void AddModeButtonStyles(Styles styles)
    {
        static Style Presenter(Func<Selector?, Selector> state, string background, string foreground) =>
            new(x => state(x.OfType<ToggleButton>().Class("resultMode")).Template().OfType<ContentPresenter>())
            {
                Setters =
                {
                    new Setter(ContentPresenter.BackgroundProperty, Brush.Parse(background)),
                    new Setter(ContentPresenter.ForegroundProperty, Brush.Parse(foreground)),
                }
            };

        styles.Add(Presenter(x => x, "#ffffff", "#1a65a7"));
        styles.Add(Presenter(x => x.Class(":pointerover"), "#e3ecf4", "#1a65a7"));
        styles.Add(Presenter(x => x.Class(":checked"), "#cbdceb", "#1a65a7"));
        styles.Add(Presenter(x => x.Class(":checked").Class(":pointerover"), "#bcd2e6", "#1a65a7"));
        styles.Add(Presenter(x => x.Class(":pressed"), "#b9cfe3", "#1a65a7"));
        styles.Add(Presenter(x => x.Class(":disabled"), "#ffffff", "#9fb8cf"));
        styles.Add(new Style(x => x.OfType<ToggleButton>().Class("resultMode").Class(":checked"))
        {
            Setters = { new Setter(TemplatedControl.FontWeightProperty, FontWeight.SemiBold) }
        });
    }

    // ------------------------------------------------------------------ column widths

    private const double MaxFitWidth = 480;

    /// <summary>Width that fits the header and the first rows' text (drag a header edge to change it).</summary>
    public static double FitWidth(ResultSeries result, int column, ResultFormatter formatter, bool asTime = false)
    {
        // Measured from character counts: fast for big results, and close enough for 13 px Segoe UI.
        const double charWidth = 7.4, headerCharWidth = 7.8, padding = 30;
        var widest = result.Columns[column].Length * headerCharWidth + padding + 12;   // room for the sort arrow
        foreach (var row in result.Values.Take(300))
        {
            if (column >= row.Count) continue;
            var text = formatter.Format(result.Columns[column], row[column], null, asTime);
            if (text != null) widest = Math.Max(widest, text.Length * charWidth + padding);
        }
        return Math.Clamp(Math.Ceiling(widest), 64, MaxFitWidth);
    }

    private static double RowNumberWidth(int rows) => Math.Max(56, rows.ToString(CultureInfo.CurrentCulture).Length * 8.5 + 30);

    /// <summary>Right-click › Auto-size Columns: back to widths that fit the content.</summary>
    public void AutoSizeColumns()
    {
        if (last == null || grid.Columns.Count == 0) return;
        var formatter = TickbirdApp.Current.Formatter;
        grid.Columns[0].Width = new DataGridLength(RowNumberWidth(last.Values.Count));
        for (var i = 0; i < last.Columns.Count && i + 1 < grid.Columns.Count; i++)
            grid.Columns[i + 1].Width = new DataGridLength(FitWidth(shown ?? last, i, formatter, timeColumns.Contains(last.Columns[i])));
    }

    /// <summary>Right-click › Show Column as Date/Time: toggles epoch-to-datetime display for the current column.</summary>
    private void ToggleTimeColumn()
    {
        if (last == null || grid.CurrentColumn == null) return;
        var column = grid.Columns.IndexOf(grid.CurrentColumn) - 1;     // column 0 is the row number
        if (column < 0 || column >= last.Columns.Count) return;
        var name = last.Columns[column];
        if (!timeColumns.Remove(name)) timeColumns.Add(name);
        ApplyFilter();
        grid.Columns[column + 1].Width = new DataGridLength(FitWidth(shown ?? last, column, TickbirdApp.Current.Formatter, timeColumns.Contains(name)));
    }

    /// <summary>The results grid (exposed for tests).</summary>
    public DataGrid ResultsGrid => grid;

    // ------------------------------------------------------------------ row filter (shared with chart and map)

    private Control BuildFilterBar()
    {
        ToolTip.SetTip(quickFilter, "Show only rows containing every word typed (in any column). The chart and map show the same rows.");
        ToolTip.SetTip(conditionsButton, "Filter on columns: speed > 3, state = work… (all conditions must be true)");
        ToolTip.SetTip(clearFilter, "Show all rows again");
        quickFilter.Text = ChartSettings.Filter.Text;
        quickFilterDelay.Tick += (_, _) =>
        {
            quickFilterDelay.Stop();
            if (quickFilter.Text == ChartSettings.Filter.Text) return;
            ChartSettings.Filter.Text = quickFilter.Text;
            FilterEdited(rebuildConditions: false);
        };
        quickFilter.TextChanged += (_, _) => { quickFilterDelay.Stop(); quickFilterDelay.Start(); };
        quickFilter.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !string.IsNullOrEmpty(quickFilter.Text)) { quickFilter.Text = ""; e.Handled = true; }
            else if (e.Key == Key.Enter) { quickFilterDelay.Stop(); ChartSettings.Filter.Text = quickFilter.Text; FilterEdited(rebuildConditions: false); e.Handled = true; }
        };
        conditionsButton.Flyout = new Flyout
        {
            Content = new ScrollViewer { MaxHeight = 420, Content = conditionsPanel },
            Placement = PlacementMode.BottomEdgeAlignedLeft
        };
        conditionsButton.Click += (_, _) => BuildConditionsPanel();
        clearFilter.Click += (_, _) => ClearFilter();

        var bar = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        bar.Children.Add(quickFilter);
        bar.Children.Add(conditionsButton);
        bar.Children.Add(clearFilter);
        bar.Children.Add(filterStatus);
        return bar;
    }

    /// <summary>Applies the shared filter to this result (and the query's other results, for combined charts and maps).</summary>
    private void ApplyFilter()
    {
        if (last == null) return;
        var filter = ChartSettings.Filter;
        var formatter = TickbirdApp.Current.Formatter;
        appliedFilterVersion = ChartSettings.FilterVersion;
        if (quickFilter.Text != filter.Text) quickFilter.Text = filter.Text;

        if (filter.IsActive)
        {
            var rows = filter.MatchingRows(last, formatter);
            shown = rows.Count == last.Values.Count ? last : new ResultSeries(last.Name, last.Columns, last.Tags, rows.Select(r => last.Values[r]).ToList());
            shownNumbers = rows.Select(r => r + 1).ToList();
            shownAll = AllResults?.Select(s => ReferenceEquals(s, last) ? shown : filter.Apply(s, formatter)).ToList();
        }
        else
        {
            shown = last;
            shownNumbers = Enumerable.Range(1, last.Values.Count).ToList();
            shownAll = AllResults;
        }

        var result = shown;
        var numbers = shownNumbers;
        grid.ItemsSource = Enumerable.Range(0, result.Values.Count).Select(i => new ResultRow(i, numbers[i], result, formatter, timeColumns)).ToList();
        chart?.SetResults(result, shownAll);
        map?.SetResults(result, shownAll);

        var active = filter.IsActive;
        var conditionCount = filter.Conditions.Count(c => c.IsComplete);
        conditionsButton.Content = conditionCount > 0 ? $"Conditions ({conditionCount}) ▾" : "Conditions ▾";
        clearFilter.IsVisible = active;
        var culture = CultureInfo.CurrentCulture;
        filterStatus.Text = active
            ? $"Showing {result.Values.Count.ToString("N0", culture)} of {last.Values.Count.ToString("N0", culture)} rows · {filter.Describe()}"
            : "";
    }

    /// <summary>After any filter edit: re-filter here; other result tabs catch up when shown.</summary>
    private void FilterEdited(bool rebuildConditions)
    {
        ChartSettings.FilterChanged();
        ApplyFilter();
        ApplyMode();
        if (rebuildConditions) BuildConditionsPanel();
    }

    /// <summary>Removes the quick text and every condition.</summary>
    public void ClearFilter()
    {
        ChartSettings.Filter.Text = null;
        ChartSettings.Filter.Conditions.Clear();
        quickFilter.Text = "";
        FilterEdited(rebuildConditions: true);
    }

    /// <summary>Sets the quick filter text and applies it at once (Enter in the box; also used by tests).</summary>
    public void SetQuickFilter(string? text)
    {
        quickFilterDelay.Stop();
        ChartSettings.Filter.Text = text;
        quickFilter.Text = text;
        FilterEdited(rebuildConditions: false);
    }

    /// <summary>Adds a column condition and applies it (also used by tests).</summary>
    public void AddFilterCondition(string column, MapConditionOperator op, string? value)
    {
        ChartSettings.Filter.Conditions.Add(new MapCondition { Column = column, Operator = op, Value = value });
        FilterEdited(rebuildConditions: true);
    }

    /// <summary>Right-click › Filter to / Exclude this value: a condition from the cell under the pointer.</summary>
    private void FilterByCell(MapConditionOperator op)
    {
        if (shown == null || grid.SelectedItem is not ResultRow row || grid.CurrentColumn == null) return;
        var column = grid.Columns.IndexOf(grid.CurrentColumn) - 1;     // column 0 is the row number
        if (column < 0 || column >= shown.Columns.Count) return;
        var cells = shown.Values[row.Index];
        var value = column < cells.Count ? cells[column] : null;
        if (value == null)
        {
            AddFilterCondition(shown.Columns[column], op == MapConditionOperator.Equals ? MapConditionOperator.IsEmpty : MapConditionOperator.IsNotEmpty, null);
            return;
        }
        var text = value switch
        {
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
        AddFilterCondition(shown.Columns[column], op, text);
    }

    private void BuildConditionsPanel()
    {
        conditionsPanel.Children.Clear();
        var filter = ChartSettings.Filter;
        var columns = (AllResults ?? (last != null ? new[] { last } : Array.Empty<ResultSeries>()))
            .SelectMany(r => r.Columns).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        conditionsPanel.Children.Add(new TextBlock
        {
            Text = "Show only rows where all conditions are true. The chart and map use the same rows.",
            FontSize = 12, Foreground = Brush.Parse("#1a65a7"), TextWrapping = TextWrapping.Wrap, MaxWidth = 480, Margin = new Thickness(0, 0, 0, 4)
        });
        for (var i = 0; i < filter.Conditions.Count; i++)
            conditionsPanel.Children.Add(ConditionEditor.Row(filter.Conditions, i, columns, rebuild => FilterEdited(rebuild), "Where"));
        var add = new Button { Content = filter.Conditions.Count == 0 ? "+ Condition" : "+ And condition", Padding = new Thickness(8, 3), FontSize = 12 };
        add.Click += (_, _) =>
        {
            filter.Conditions.Add(new MapCondition { Column = columns.FirstOrDefault(c => !string.Equals(c, "time", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault() });
            BuildConditionsPanel();   // a new, empty condition filters nothing until a value is entered
        };
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
        buttons.Children.Add(add);
        if (filter.IsActive)
        {
            var clear = new Button { Content = "Clear filter", Padding = new Thickness(8, 3), FontSize = 12 };
            clear.Click += (_, _) => ClearFilter();
            buttons.Children.Add(clear);
        }
        conditionsPanel.Children.Add(buttons);
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private static MenuItem Item(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await action();
        return item;
    }

    private IEnumerable<int> CurrentRow() => grid.SelectedItem is ResultRow r ? new[] { r.Index } : Array.Empty<int>();

    private HashSet<int> SelectedIndices() => grid.SelectedItems.OfType<ResultRow>().Select(r => r.Index).ToHashSet();

    private async void CopyCell()
    {
        if (grid.SelectedItem is not ResultRow row || grid.CurrentColumn == null) return;
        var text = row[grid.Columns.IndexOf(grid.CurrentColumn)] ?? string.Empty;
        await SetClipboard(text);
    }

    private async void CopyRows(int[] rows)
    {
        if (shown == null || rows.Length == 0) return;
        await SetClipboard(ResultExporter.ToTabSeparated(shown, TickbirdApp.Current.Formatter, rows));
    }

    private async Task SetClipboard(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null) await clipboard.SetTextAsync(text);
    }

    private async Task Export(bool selectedOnly, bool csv)
    {
        if (shown == null) return;
        var selected = SelectedIndices();
        if (selectedOnly && selected.Count == 0) return;
        var name = ResultExporter.SafeFileName($"{Client?.Connection.Name}_{Database}.{(csv ? "csv" : "json")}");
        var path = await FilePicker.SaveAsync(this, name, csv ? "CSV files" : "JSON files", csv ? "*.csv" : "*.json");
        if (path == null) return;
        var result = shown;   // export what is shown: the filtered rows
        var formatter = TickbirdApp.Current.Formatter;
        try
        {
            await Task.Run(() =>
            {
                using var writer = new StreamWriter(path, false, new System.Text.UTF8Encoding(false));
                if (csv) ResultExporter.WriteCsv(writer, result, formatter, selectedOnly ? selected : null);
                else ResultExporter.WriteJson(writer, result, selectedOnly ? selected : null);
            });
        }
        catch (Exception ex)
        {
            if (TopLevel.GetTopLevel(this) is Window w) await Dialogs.ShowExceptionAsync(w, ex);
        }
    }
}
