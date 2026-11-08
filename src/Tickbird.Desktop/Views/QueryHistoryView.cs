using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tickbird.Core.History;
using Tickbird.Core.Settings;
using Tickbird.Desktop.Services;
using Tickbird.Desktop.Views.Requests;

namespace Tickbird.Desktop.Views;

/// <summary>
/// Query History tab: every query run from an editor, newest first. Search by text, filter by connection,
/// and reopen a query in a new tab (double-click or Enter).
/// </summary>
public sealed class QueryHistoryView : UserControl
{
    private const string AllConnections = "All connections";

    // Logical column order (cell positions). The order shown can be changed by dragging the headers and is saved.
    private static readonly string[] DefaultColumns = { "Run at", "Query", "Caption", "★", "Connection", "Database", "Status", "Duration", "Rows", "Tags" };
    private const int RunAtColumn = 0, QueryColumn = 1, CaptionColumn = 2, FavoriteColumn = 3, TagsColumn = 9;
    private bool applyingColumnOrder;
    private static readonly IBrush Accent = Brush.Parse("#1a65a7");

    private readonly QueryHistory history;
    private readonly Func<QueryHistoryEntry, Task> open;
    private readonly Func<QueryHistoryEntry, Task>? runAgain;
    private readonly DataGrid grid = GridFactory.Create();
    private readonly TextBox search = new() { Watermark = "Search captions, queries, connections and databases…", MinWidth = 280, FontSize = 12 };
    private readonly ComboBox connectionBox = new() { MinWidth = 180, FontSize = 12 };
    private readonly CheckBox favoritesOnly = new() { Content = "Favorites only", FontSize = 12 };
    private readonly Button favoriteButton = new() { Padding = new Thickness(10, 4), FontSize = 12 };
    private readonly TextBox preview = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 120, FontSize = 12,
        FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"), Margin = new Thickness(0, 8, 0, 0)
    };
    private readonly TextBlock status = new() { FontSize = 12, Foreground = Accent, Margin = new Thickness(2, 6, 2, 0) };
    private readonly DispatcherTimer searchDelay = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private bool binding;

    public QueryHistoryView(QueryHistory history, Func<QueryHistoryEntry, Task> open, Func<QueryHistoryEntry, Task>? runAgain = null)
    {
        this.history = history;
        this.open = open;
        this.runAgain = runAgain;
        Background = Brush.Parse("#F1F7FD");

        GridFactory.SetColumns(grid, DefaultColumns);
        grid.Columns[RunAtColumn].Width = new DataGridLength(150);
        grid.Columns[QueryColumn].Width = new DataGridLength(1, DataGridLengthUnitType.Star);
        grid.Columns[QueryColumn].MinWidth = 200;
        grid.Columns[CaptionColumn].Width = new DataGridLength(180);
        grid.Columns[FavoriteColumn].Width = new DataGridLength(34);
        grid.Columns[TagsColumn].Width = new DataGridLength(120);
        ApplySavedColumnOrder();
        grid.ColumnDisplayIndexChanged += (_, _) => SaveColumnOrder();
        // Double-clicking the caption renames it; double-clicking anywhere else reopens the query.
        grid.DoubleTapped += async (_, e) =>
        {
            if (e.Source is Visual source && source.FindAncestorOfType<DataGridCell>() != null
                && ReferenceEquals(grid.CurrentColumn, grid.Columns[CaptionColumn]))
                await EditCaptionAsync();
            else await OpenSelectedAsync();
        };
        grid.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; await OpenSelectedAsync(); }
            else if (e.Key == Key.F2) { e.Handled = true; await EditCaptionAsync(); }
            else if (e.Key == Key.Delete) { e.Handled = true; await DeleteSelectedAsync(); }
        };
        grid.SelectionChanged += (_, _) =>
        {
            var entry = Selected().FirstOrDefault();
            preview.Text = entry == null ? "" : (string.IsNullOrWhiteSpace(entry.Caption) ? "" : "-- " + entry.Caption + "\n\n") + entry.Query + (entry.Error != null ? "\n\n-- Error: " + entry.Error : "");
            UpdateFavoriteButton();
        };

        searchDelay.Tick += (_, _) => { searchDelay.Stop(); Refresh(); };
        search.TextChanged += (_, _) => { searchDelay.Stop(); searchDelay.Start(); };
        search.KeyDown += (_, e) => { if (e.Key == Key.Escape) search.Text = ""; };
        connectionBox.SelectionChanged += (_, _) => { if (!binding) Refresh(); };
        favoritesOnly.IsCheckedChanged += (_, _) => Refresh();
        favoriteButton.Click += (_, _) => ToggleFavorite();
        ToolTip.SetTip(favoriteButton, "Mark the selected queries as favorites, or remove the mark");
        ToolTip.SetTip(favoritesOnly, "Show only your favorite queries");
        UpdateFavoriteButton();

        var openButton = Action("Open in New Query", "Open the selected query in a new query tab (double-click or Enter)", async () => await OpenSelectedAsync());
        var copy = Action("Copy", "Copy the selected queries", async () => await CopySelectedAsync());
        var runButton = Action("Run Again", "Open the selected query in a new tab and run it at once", async () => await RunSelectedAsync());
        var tags = Action("Tags…", "Label the selected queries with tags you can search for", async () => await EditTagsAsync());
        var caption = Action("Caption…", "Give the selected query a title you can search for (F2)", async () => await EditCaptionAsync());
        var delete = Action("Delete", "Remove the selected entries (Delete)", DeleteSelectedAsync);
        var clear = Action("Clear All", "Remove every entry from the history", ClearAllAsync);

        var export = Action("Export…", "Save the selected queries, or all of them when none are selected, to a file", async () => await ExportAsync());
        var import = Action("Import…", "Add the queries of an exported history file", async () => await ImportAsync());

        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var control in new Control[] { search, connectionBox, favoritesOnly, favoriteButton, caption, tags, runButton, openButton, copy, delete, clear, export, import })
        {
            control.Margin = new Thickness(0, 2, 8, 2);
            control.VerticalAlignment = VerticalAlignment.Center;
            toolbar.Children.Add(control);
        }

        var layout = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(status, Dock.Bottom);
        DockPanel.SetDock(preview, Dock.Bottom);
        layout.Children.Add(toolbar);
        layout.Children.Add(status);
        layout.Children.Add(preview);
        layout.Children.Add(new Border
        {
            Background = Brushes.White, BorderBrush = Brush.Parse("#e5eef5"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5), ClipToBounds = true, Child = grid
        });
        Content = layout;

        Refresh();
        AttachedToVisualTree += (_, _) => { history.Changed += Refresh; Refresh(); };
        DetachedFromVisualTree += (_, _) => history.Changed -= Refresh;
    }

    /// <summary>Puts the columns in the order saved in the settings (columns it does not know keep their place after).</summary>
    private void ApplySavedColumnOrder()
    {
        var saved = TickbirdApp.Current.Settings.QueryHistoryColumnOrder ?? new List<string>();
        var order = saved.Where(h => DefaultColumns.Contains(h)).Distinct().ToList();
        if (order.Count == 0) return;
        order.AddRange(DefaultColumns.Where(h => !order.Contains(h)));
        applyingColumnOrder = true;
        try
        {
            for (var i = 0; i < order.Count; i++)
                grid.Columns.First(c => (c.Header as string) == order[i]).DisplayIndex = i;
        }
        finally
        {
            applyingColumnOrder = false;
        }
    }

    private void SaveColumnOrder()
    {
        if (applyingColumnOrder) return;
        var app = TickbirdApp.Current;
        app.Settings.QueryHistoryColumnOrder = grid.Columns.OrderBy(c => c.DisplayIndex).Select(c => (string)c.Header!).ToList();
        app.Save();
    }

    /// <summary>The column headers from left to right as shown (exposed for tests).</summary>
    public IReadOnlyList<string> ColumnOrder => grid.Columns.OrderBy(c => c.DisplayIndex).Select(c => (string)c.Header!).ToList();

    /// <summary>The rows shown (exposed for tests).</summary>
    public IReadOnlyList<QueryHistoryEntry> Shown { get; private set; } = Array.Empty<QueryHistoryEntry>();

    /// <summary>Sets the search text and refreshes at once (for tests).</summary>
    public void SearchFor(string text)
    {
        search.Text = text;
        searchDelay.Stop();
        Refresh();
    }

    public void Refresh()
    {
        BindConnections();
        var name = connectionBox.SelectedItem as string;
        var connectionId = name == null || name == AllConnections
            ? null
            : history.Entries.FirstOrDefault(e => e.ConnectionName == name)?.ConnectionId;
        Shown = history.Search(search.Text, connectionId, favoritesOnly.IsChecked == true);
        var settings = TickbirdApp.Current.Settings;
        var timeFormat = $"{TickbirdSettings.ResolveDateFormat(settings.DateFormat)} {settings.TimeFormat}";
        var keepSelected = Selected().Select(e => e.Id).ToHashSet();
        GridFactory.SetRows(grid, Shown.Select(e => new GridRow(new[]
        {
            e.RunAtUtc.ToLocalTime().ToString(timeFormat, CultureInfo.CurrentCulture),
            e.OneLine.Length > 400 ? e.OneLine[..399] + "…" : e.OneLine,
            e.Caption,
            e.IsFavorite ? "★" : "",
            e.ConnectionName,
            e.Database,
            StatusText(e.Status),
            FormatDuration(e.DurationMs),
            e.Status == QueryRunStatus.Succeeded ? e.Rows.ToString("N0", CultureInfo.CurrentCulture) : "",
            string.Join(", ", e.Tags ?? new List<string>())
        }, e)));
        if (keepSelected.Count > 0)
            foreach (var row in grid.ItemsSource!.Cast<GridRow>().Where(r => r.Tag is QueryHistoryEntry e && keepSelected.Contains(e.Id)))
                grid.SelectedItems.Add(row);
        UpdateFavoriteButton();
        status.Text = history.Entries.Count == 0
            ? (settings.KeepQueryHistory ? "No queries yet. Queries you run from a query tab appear here." : "Query history is off (Settings › Keep Query History).")
            : $"{Shown.Count:N0} of {history.Entries.Count:N0} queries" + (history.FavoriteCount > 0 ? $" · {history.FavoriteCount:N0} favorite{(history.FavoriteCount == 1 ? "" : "s")}" : "");
    }

    private void BindConnections()
    {
        binding = true;
        try
        {
            var selected = connectionBox.SelectedItem as string ?? AllConnections;
            var names = new List<string> { AllConnections };
            names.AddRange(history.Entries.Select(e => e.ConnectionName).OfType<string>().Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            connectionBox.ItemsSource = names;
            connectionBox.SelectedItem = names.Contains(selected) ? selected : AllConnections;
        }
        finally
        {
            binding = false;
        }
    }

    private List<QueryHistoryEntry> Selected() =>
        grid.SelectedItems.OfType<GridRow>().Select(r => r.Tag).OfType<QueryHistoryEntry>().ToList();

    private async Task OpenSelectedAsync()
    {
        foreach (var entry in Selected().Take(10)) await open(entry);
    }

    private async Task RunSelectedAsync()
    {
        var run = runAgain ?? open;
        foreach (var entry in Selected().Take(3)) await run(entry);
    }

    /// <summary>Asks for comma separated tags for the selected queries (blank removes them).</summary>
    public async Task EditTagsAsync()
    {
        var selected = Selected();
        if (selected.Count == 0 || TopLevel.GetTopLevel(this) is not Window owner) return;
        var initial = selected.Count == 1 ? string.Join(", ", selected[0].Tags ?? new List<string>()) : "";
        var text = await Dialogs.PromptTextAsync(owner, "Query Tags",
            selected.Count == 1
                ? "Enter tags separated by commas. You can search the history by them. Leave it empty to remove the tags."
                : $"Enter tags separated by commas. They replace the tags of the {selected.Count:N0} selected queries.",
            initial, ok: "Save");
        if (text != null) history.SetTags(selected.Select(e => e.Id), text.Split(','));
    }

    private async Task CopySelectedAsync()
    {
        var text = string.Join(Environment.NewLine + Environment.NewLine, Selected().Select(e => e.Query.TrimEnd()));
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null && text.Length > 0) await clipboard.SetTextAsync(text);
    }

    private async Task ExportAsync()
    {
        var selected = Selected();
        var file = await FilePicker.SaveAsync(this, $"tickbird-query-history-{DateTime.Now:yyyyMMdd}.json", "Query history", "*.json");
        if (file == null) return;
        try
        {
            var count = history.Export(file, selected.Count > 0 ? selected : null);
            status.Text = $"Exported {count:N0} {(count == 1 ? "query" : "queries")} to {file}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (TopLevel.GetTopLevel(this) is Window owner) await Dialogs.ShowErrorAsync(owner, ex.Message, "Export Failed");
        }
    }

    private async Task ImportAsync()
    {
        var file = await FilePicker.OpenAsync(this, "Query history", "*.json");
        if (file == null) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        try
        {
            var result = history.Import(file);
            Refresh();
            status.Text = $"Imported {result.Added:N0} new and {result.Updated:N0} updated queries ({result.Skipped:N0} skipped).";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            if (owner != null) await Dialogs.ShowErrorAsync(owner, ex.Message, "Import Failed");
        }
    }

    /// <summary>Asks for a caption for the selected query (blank removes it).</summary>
    public async Task EditCaptionAsync()
    {
        var entry = Selected().FirstOrDefault();
        if (entry == null || TopLevel.GetTopLevel(this) is not Window owner) return;
        var text = await Dialogs.PromptTextAsync(owner, "Query Caption",
            "Enter a caption for this query. You can search the history by it. Leave it empty to remove the caption.", entry.Caption, ok: "Save");
        if (text != null) history.SetCaption(entry.Id, text);
    }

    /// <summary>Marks the selected queries as favorites, or removes the mark when they all are favorites already.</summary>
    public void ToggleFavorite()
    {
        var selected = Selected();
        if (selected.Count == 0) return;
        history.SetFavorite(selected.Select(e => e.Id), favorite: !selected.All(e => e.IsFavorite));
    }

    private void UpdateFavoriteButton()
    {
        var selected = Selected();
        var allFavorites = selected.Count > 0 && selected.All(e => e.IsFavorite);
        favoriteButton.Content = allFavorites ? "★ Remove Favorite" : "☆ Add Favorite";
        favoriteButton.IsEnabled = selected.Count > 0;
    }

    /// <summary>
    /// Deletes the selected queries. Favorites among them are kept unless the user ticks the box in the
    /// confirmation (shown only when the selection contains favorites).
    /// </summary>
    public async Task DeleteSelectedAsync()
    {
        var selected = Selected();
        if (selected.Count == 0) return;
        var favorites = selected.Count(e => e.IsFavorite);
        var captioned = selected.Count(e => !string.IsNullOrWhiteSpace(e.Caption));
        if (favorites == 0)
        {
            // A caption is work the user typed, so deleting captioned queries asks first.
            if (captioned > 0 && TopLevel.GetTopLevel(this) is Window confirmOwner)
            {
                var sure = await Dialogs.ConfirmAsync(confirmOwner, "Delete Captioned Queries",
                    captioned == 1 && selected.Count == 1
                        ? $"Delete the query \"{selected[0].Caption}\"? This cannot be undone."
                        : $"{captioned:N0} of the {selected.Count:N0} selected queries have a caption. Delete them anyway? This cannot be undone.");
                if (!sure) return;
            }
            history.Remove(selected.Select(e => e.Id));
            return;
        }

        var ids = selected.Select(e => e.Id).ToList();
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null) { history.Remove(ids, includeFavorites: false); return; }

        if (favorites == selected.Count)
        {
            var all = await Dialogs.ConfirmWithOptionAsync(owner, "Delete Favorite Queries",
                favorites == 1 ? "The selected query is a favorite. Delete it anyway?" : $"All {favorites:N0} selected queries are favorites. Delete them anyway?",
                option: null);
            if (all.Confirmed) history.Remove(ids);
            return;
        }

        var result = await Dialogs.ConfirmWithOptionAsync(owner, "Delete Queries",
            $"Delete the {selected.Count:N0} selected queries? {favorites:N0} of them {(favorites == 1 ? "is a favorite" : "are favorites")} and will be kept unless you tick the box below.",
            favorites == 1 ? "Also delete the favorite query" : $"Also delete the {favorites:N0} favorite queries");
        if (result.Confirmed) history.Remove(ids, includeFavorites: result.Checked);
    }

    /// <summary>Clears the history after asking. Favorites are kept unless the user ticks the box.</summary>
    public async Task ClearAllAsync()
    {
        var total = history.Entries.Count;
        if (total == 0) return;
        var favorites = history.FavoriteCount;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null) { history.Clear(includeFavorites: false); return; }

        var others = total - favorites;
        if (favorites == 0 || others == 0)
        {
            var all = await Dialogs.ConfirmWithOptionAsync(owner, "Clear Query History",
                favorites == 0
                    ? $"Delete all {total:N0} queries from the history? This cannot be undone."
                    : $"Every query in the history is a favorite. Delete all {total:N0} of them? This cannot be undone.",
                option: null, ok: "Clear All");
            if (all.Confirmed) history.Clear();
            return;
        }

        var result = await Dialogs.ConfirmWithOptionAsync(owner, "Clear Query History",
            $"Delete the {others:N0} queries that are not favorites? Your {favorites:N0} favorite{(favorites == 1 ? "" : "s")} will be kept unless you tick the box below. This cannot be undone.",
            $"Also delete the {favorites:N0} favorite{(favorites == 1 ? "" : "s")}", ok: "Clear All");
        if (result.Confirmed) history.Clear(includeFavorites: result.Checked);
    }

    private static string StatusText(QueryRunStatus status) => status switch
    {
        QueryRunStatus.Failed => "Error",
        QueryRunStatus.Cancelled => "Cancelled",
        QueryRunStatus.TimedOut => "Timed out",
        _ => "OK"
    };

    private static string FormatDuration(double ms) =>
        ms < 1000 ? $"{ms:0} ms" : ms < 60000 ? $"{ms / 1000:0.0} s" : $"{(int)(ms / 60000)} min {ms % 60000 / 1000:0} s";

    private static Button Action(string text, string tip, Func<Task> action)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 4), FontSize = 12 };
        ToolTip.SetTip(button, tip);
        button.Click += async (_, _) => await action();
        return button;
    }
}
