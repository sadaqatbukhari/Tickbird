using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tickbird.Core.History;
using Tickbird.Desktop.Services;
using Tickbird.Desktop.Views;
using Tickbird.Desktop.Views.Requests;

namespace Tickbird.Desktop.Tests;

public sealed class QueryHistoryFavoritesViewTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "tickbird-favview-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    private (Window Host, QueryHistoryView View, QueryHistory History, DataGrid Grid) Create(params string[] queries)
    {
        TickbirdApp.Current = new TickbirdApp(new Tickbird.Core.Settings.SettingsStore(Path.Combine(dir, "settings.json")));
        var history = new QueryHistory(Path.Combine(dir, "query-history.json"));
        foreach (var query in queries)
            history.Add(new QueryHistoryEntry { ConnectionId = "1", ConnectionName = "Local", Database = "db", Query = query, RunAtUtc = DateTime.UtcNow });
        var view = new QueryHistoryView(history, _ => Task.CompletedTask);
        var host = new Window { Content = view, Width = 1100, Height = 600 };
        host.Show();
        Pump();
        return (host, view, history, view.GetVisualDescendants().OfType<DataGrid>().First());
    }

    private static void Pump()
    {
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
    }

    private static void Select(DataGrid grid, params string[] queries)
    {
        Pump();
        grid.UpdateLayout();
        grid.SelectedItems.Clear();
        foreach (var row in grid.ItemsSource!.Cast<GridRow>())
            if (queries.Contains(((QueryHistoryEntry)row.Tag!).Query)) grid.SelectedItems.Add(row);
        Pump();
    }

    private static Dialogs.OptionConfirmWindow Dialog(Window host) =>
        Assert.IsType<Dialogs.OptionConfirmWindow>(Assert.Single(host.OwnedWindows));

    [AvaloniaFact]
    public void Columns_StartInTheRequestedOrder_AndAReorderedLayoutIsSavedAndRestored()
    {
        var (host, view, _, grid) = Create("SELECT 1");
        Assert.Equal(new[] { "Run at", "Query", "Caption", "★", "Connection", "Database", "Status", "Duration", "Rows", "Tags" }, view.ColumnOrder);

        // The user drags "Tags" to the front.
        grid.Columns.First(c => (string?)c.Header == "Tags").DisplayIndex = 0;
        Pump();
        Assert.Equal("Tags", TickbirdApp.Current.Settings.QueryHistoryColumnOrder[0]);
        host.Close();

        // A new history tab (as after a restart) uses the saved order.
        var again = new QueryHistoryView(new QueryHistory(null), _ => Task.CompletedTask);
        var second = new Window { Content = again, Width = 1100, Height = 600 };
        second.Show();
        Pump();
        Assert.Equal("Tags", again.ColumnOrder[0]);
        Assert.Equal("Run at", again.ColumnOrder[1]);
        second.Close();
    }

    [AvaloniaFact]
    public void FavoriteButton_MarksTheSelection_AndTheGridShowsAStar()
    {
        var (host, view, history, grid) = Create("SELECT 1", "SELECT 2");
        Select(grid, "SELECT 1");

        view.ToggleFavorite();
        Pump();

        Assert.True(history.Entries.Single(e => e.Query == "SELECT 1").IsFavorite);
        var starred = grid.ItemsSource!.Cast<GridRow>().Single(r => ((QueryHistoryEntry)r.Tag!).Query == "SELECT 1");
        Assert.Equal("★", starred[3]);

        view.ToggleFavorite();    // toggling again removes the mark
        Assert.False(history.Entries.Single(e => e.Query == "SELECT 1").IsFavorite);
        host.Close();
    }

    [AvaloniaFact]
    public void FavoritesOnly_FiltersTheList()
    {
        var (host, view, history, grid) = Create("SELECT 1", "SELECT 2");
        history.SetFavorite(history.Entries.Where(e => e.Query == "SELECT 2").Select(e => e.Id), true);
        Pump();
        Assert.Equal(2, view.Shown.Count);

        var box = view.GetVisualDescendants().OfType<CheckBox>().Single(c => (string?)c.Content == "Favorites only");
        box.IsChecked = true;
        Pump();

        Assert.Equal(new[] { "SELECT 2" }, view.Shown.Select(e => e.Query));
        host.Close();
    }

    [AvaloniaFact]
    public async Task Delete_WithoutFavorites_DoesNotAsk()
    {
        var (host, view, history, grid) = Create("SELECT 1", "SELECT 2");
        Select(grid, "SELECT 1");

        await view.DeleteSelectedAsync();

        Assert.Empty(host.OwnedWindows);
        Assert.Equal(new[] { "SELECT 2" }, history.Entries.Select(e => e.Query));
        host.Close();
    }

    [AvaloniaFact]
    public async Task Delete_WithFavoritesSelected_KeepsThemUnlessTheBoxIsTicked()
    {
        var (host, view, history, grid) = Create("plain", "star");
        history.SetFavorite(history.Entries.Where(e => e.Query == "star").Select(e => e.Id), true);
        Pump();
        Select(grid, "plain", "star");

        var pending = view.DeleteSelectedAsync();
        var dialog = Dialog(host);
        Assert.NotNull(dialog.OptionBox.Content);
        Assert.False(dialog.OptionBox.IsChecked);        // favorites are kept by default
        dialog.ConfirmButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await pending;
        Assert.Equal(new[] { "star" }, history.Entries.Select(e => e.Query));

        Select(grid, "star");
        pending = view.DeleteSelectedAsync();
        dialog = Dialog(host);
        Assert.Null(dialog.OptionBox.Content);           // only favorites selected: no check box, just a confirmation
        dialog.ConfirmButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await pending;
        Assert.Empty(history.Entries);
        host.Close();
    }

    [AvaloniaFact]
    public async Task Delete_WithTheBoxTicked_AlsoDeletesFavorites_AndCancelDeletesNothing()
    {
        var (host, view, history, grid) = Create("plain", "star");
        history.SetFavorite(history.Entries.Where(e => e.Query == "star").Select(e => e.Id), true);
        Pump();
        Select(grid, "plain", "star");

        var pending = view.DeleteSelectedAsync();
        Dialog(host).Close(false);
        await pending;
        Assert.Equal(2, history.Entries.Count);

        pending = view.DeleteSelectedAsync();
        var dialog = Dialog(host);
        dialog.OptionBox.IsChecked = true;
        dialog.ConfirmButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await pending;
        Assert.Empty(history.Entries);
        host.Close();
    }

    [AvaloniaFact]
    public async Task ClearAll_AsksAndKeepsFavoritesUnlessTheBoxIsTicked()
    {
        var (host, view, history, grid) = Create("a", "b", "star");
        history.SetFavorite(history.Entries.Where(e => e.Query == "star").Select(e => e.Id), true);

        var pending = view.ClearAllAsync();
        var dialog = Dialog(host);
        Assert.NotNull(dialog.OptionBox.Content);
        dialog.Close(false);                                    // cancel: nothing is deleted
        await pending;
        Assert.Equal(3, history.Entries.Count);

        pending = view.ClearAllAsync();
        dialog = Dialog(host);
        dialog.ConfirmButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await pending;
        Assert.Equal(new[] { "star" }, history.Entries.Select(e => e.Query));

        pending = view.ClearAllAsync();                          // only a favorite is left: asks without the box
        dialog = Dialog(host);
        Assert.Null(dialog.OptionBox.Content);
        dialog.ConfirmButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await pending;
        Assert.Empty(history.Entries);
        host.Close();
    }

    [AvaloniaFact]
    public async Task ClearAll_WithTheBoxTicked_RemovesTheFavoritesToo()
    {
        var (host, view, history, grid) = Create("a", "star");
        history.SetFavorite(history.Entries.Where(e => e.Query == "star").Select(e => e.Id), true);

        var pending = view.ClearAllAsync();
        var dialog = Dialog(host);
        dialog.OptionBox.IsChecked = true;
        dialog.ConfirmButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await pending;

        Assert.Empty(history.Entries);
        host.Close();
    }
}
