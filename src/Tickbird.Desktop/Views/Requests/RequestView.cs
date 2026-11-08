using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Tickbird.Core.Data;
using Tickbird.Core.Navigation;
using Tickbird.Core.Query;
using Tickbird.Desktop.Services;

namespace Tickbird.Desktop.Views.Requests;

/// <summary>A grid row backed by string cells, bound by index ("[0]").</summary>
public sealed class GridRow
{
    private readonly string?[] cells;
    public GridRow(string?[] cells, object? tag = null) { this.cells = cells; Tag = tag; }
    public object? Tag { get; }
    public string? this[int index] => index < cells.Length ? cells[index] : null;
}

public static class GridFactory
{
    public const string CheckMark = "✓";

    public static DataGrid Create()
    {
        var grid = new DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = false,
            CanUserResizeColumns = true,
            CanUserReorderColumns = true,
            SelectionMode = DataGridSelectionMode.Extended,
            GridLinesVisibility = DataGridGridLinesVisibility.All,
            HeadersVisibility = DataGridHeadersVisibility.Column,
        };
        TickbirdTheme.StyleGrid(grid);
        return grid;
    }

    public static void SetColumns(DataGrid grid, IEnumerable<string> headers)
    {
        grid.Columns.Clear();
        var i = 0;
        foreach (var header in headers)
            grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding($"[{i++}]"), IsReadOnly = true });
    }

    public static void SetRows(DataGrid grid, IEnumerable<GridRow> rows) =>
        grid.ItemsSource = rows.ToList();

    public static void Fill(DataGrid grid, TableData table, bool rowNumbers = true)
    {
        SetColumns(grid, rowNumbers ? new[] { "#" }.Concat(table.Columns) : table.Columns);
        SetRows(grid, table.Rows.Select((r, i) => new GridRow(
            (rowNumbers ? new[] { (i + 1).ToString() } : Array.Empty<string>()).Concat(r).ToArray(), r)));
    }
}

public static class FilePicker
{
    public static async Task<string?> SaveAsync(Visual anchor, string suggestedName, string filterName, string pattern)
    {
        var top = TopLevel.GetTopLevel(anchor);
        if (top == null) return null;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            DefaultExtension = pattern.TrimStart('*', '.'),
            FileTypeChoices = new[]
            {
                new FilePickerFileType(filterName) { Patterns = new[] { pattern } },
                new FilePickerFileType("All files") { Patterns = new[] { "*" } }
            }
        });
        return file?.TryGetLocalPath();
    }

    public static async Task<string?> OpenAsync(Visual anchor, string filterName, params string[] patterns)
    {
        var top = TopLevel.GetTopLevel(anchor);
        if (top == null) return null;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(filterName) { Patterns = patterns },
                new FilePickerFileType("All files") { Patterns = new[] { "*" } }
            }
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }
}

/// <summary>Base class for tabs that load data from an InfluxDB server (port of RequestControl).</summary>
public abstract class RequestView : UserControl
{
    protected RequestView()
    {
        Background = TickbirdTheme.Canvas;
    }

    public DatabaseClient Client { get; set; } = null!;
    public string? Database { get; set; }

    /// <summary>Loads or reloads the tab's data.</summary>
    public abstract Task ExecuteAsync();

    protected Window? OwnerWindow => TopLevel.GetTopLevel(this) as Window;

    protected async Task ShowError(string message, string title = "Error")
    {
        if (OwnerWindow != null) await Dialogs.ShowErrorAsync(OwnerWindow, message, title);
    }

    protected async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            if (OwnerWindow != null) await Dialogs.ShowExceptionAsync(OwnerWindow, ex);
        }
    }

    protected static Button ToolButton(string text, string? icon = null, string? tip = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var bitmap = icon != null ? ShellIcons.Get(icon) : null;
        if (bitmap != null) content.Children.Add(new Image { Source = bitmap, Width = 16, Height = 16 });
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = content, Padding = new Thickness(8, 3) };
        if (tip != null) ToolTip.SetTip(button, tip);
        return button;
    }
}

/// <summary>A request that shows a single table with export (port of MeasurementControl).</summary>
public abstract class ListRequestView : RequestView
{
    protected readonly DataGrid Grid = GridFactory.Create();
    protected readonly StackPanel Header = new() { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 12) };
    private readonly TextBlock status = new() { Margin = new Thickness(4, 10, 4, 2), FontSize = 12, Foreground = TickbirdTheme.Muted };
    private TableData? lastTable;

    /// <summary>The page layout: header on top, status at the bottom, the grid in the middle.</summary>
    protected readonly DockPanel Layout = new();

    protected ListRequestView()
    {
        var export = new Button { Content = "Export ▾", Padding = new Thickness(8, 3) };
        export.Flyout = new MenuFlyout
        {
            Items =
            {
                MenuItem("Export All to CSV…", () => Export(false, true)),
                MenuItem("Export All to JSON…", () => Export(false, false)),
                MenuItem("Export Selected to CSV…", () => Export(true, true)),
                MenuItem("Export Selected to JSON…", () => Export(true, false)),
            }
        };
        var refresh = ToolButton("Refresh", "Refresh");
        refresh.Click += async (_, _) => await Guard(ExecuteAsync);
        Header.Children.Add(refresh);
        Header.Children.Add(export);

        var panel = Layout;
        DockPanel.SetDock(Header, Dock.Top);
        DockPanel.SetDock(status, Dock.Bottom);
        panel.Children.Add(Header);
        panel.Children.Add(status);
        panel.Children.Add(Grid);
        Content = TickbirdTheme.Card(panel);
    }

    public abstract string ExportFileNameStem { get; }

    protected abstract Task<TableData> LoadAsync();

    /// <summary>Hook that lets a derived class add controls into <see cref="Header"/> after the standard ones.</summary>
    protected void ShowTable(TableData table)
    {
        lastTable = table;
        GridFactory.Fill(Grid, table);
        status.Text = $"{table.Rows.Count} row(s)";
    }

    public override async Task ExecuteAsync() => ShowTable(await LoadAsync());

    private MenuItem MenuItem(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await Guard(action);
        return item;
    }

    private async Task Export(bool selectedOnly, bool csv)
    {
        if (lastTable == null) return;
        var selected = Grid.SelectedItems.OfType<GridRow>().Select(r => lastTable.Rows.ToList().FindIndex(x => ReferenceEquals(x, r.Tag))).Where(i => i >= 0).ToHashSet();
        if (selectedOnly && selected.Count == 0) return;
        var name = ResultExporter.SafeFileName($"{Client.Connection.Name}_{Database}_{ExportFileNameStem}.{(csv ? "csv" : "json")}");
        var path = await FilePicker.SaveAsync(this, name, csv ? "CSV files" : "JSON files", csv ? "*.csv" : "*.json");
        if (path == null) return;
        var table = lastTable;
        await Task.Run(() =>
        {
            using var writer = new StreamWriter(path, false, new System.Text.UTF8Encoding(false));
            if (csv) ResultExporter.WriteCsv(writer, table, selectedOnly ? selected : null);
            else ResultExporter.WriteJson(writer, table, selectedOnly ? selected : null);
        });
    }
}
