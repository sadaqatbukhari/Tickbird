using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Tickbird.Core.Settings;
using Tickbird.Desktop.Services;
using Tickbird.Desktop.ViewModels;
using Tickbird.Desktop.Views;
using Tickbird.Desktop.Views.Requests;

namespace Tickbird.Desktop.Tests;

public sealed class MainWindowTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "tickbird-ui-" + Guid.NewGuid().ToString("N"));

    private MainWindow Create()
    {
        var app = new TickbirdApp(new SettingsStore(Path.Combine(dir, "settings.json")));
        TickbirdApp.Current = app;
        return new MainWindow(app, showConnectionsOnStart: false);
    }

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    [AvaloniaFact]
    public void Window_HasMenuToolbarTreeTabsAndStatusBar()
    {
        var window = Create();
        window.Show();

        Assert.NotNull(window.FindControl<Menu>("MainMenu"));
        var toolbar = window.FindControl<StackPanel>("ToolBar")!;
        Assert.Equal(23, toolbar.Children.OfType<Button>().Count());   // includes Stop Query
        Assert.Equal(3, toolbar.Children.OfType<Separator>().Count());
        Assert.NotNull(window.FindControl<TreeView>("Tree"));
        Assert.NotNull(window.FindControl<TabControl>("Tabs"));
        window.Close();
    }

    [AvaloniaFact]
    public void Toolbar_StartsWithOnlyManageConnectionsEnabled()
    {
        var window = Create();
        window.Show();
        var buttons = window.FindControl<StackPanel>("ToolBar")!.Children.OfType<Button>().ToList();
        Assert.True(buttons[0].IsEnabled);
        Assert.All(buttons.Skip(1), b => Assert.False(b.IsEnabled));
        window.Close();
    }

    [AvaloniaFact]
    public void QueryMenu_StartsDisabled_WithLegacyShortcuts()
    {
        var window = Create();
        window.Show();
        Assert.False(window.FindControl<MenuItem>("MenuRunQuery")!.IsEnabled);
        Assert.False(window.FindControl<MenuItem>("MenuNewQuery")!.IsEnabled);
        Assert.Equal("Ctrl+R", window.FindControl<MenuItem>("MenuRunQuery")!.HotKey?.ToString());
        Assert.Equal("F5", window.FindControl<MenuItem>("MenuRefresh")!.HotKey?.ToString());
        window.Close();
    }

    [AvaloniaFact]
    public void TabControl_ShowsOneTabPerViewModelTab()
    {
        var window = Create();
        window.Show();
        var vm = (MainWindowViewModel)window.DataContext!;
        vm.AddTab(new TabViewModel("a", "First", TabKind.Request, "1", "Users", new TextBlock { Text = "one" }));
        vm.AddTab(new TabViewModel("b", "Second", TabKind.Request, "1", "Users", new TextBlock { Text = "two" }));
        Assert.Equal(2, window.FindControl<TabControl>("Tabs")!.ItemCount);
        window.Close();
    }

    [AvaloniaFact]
    public void SettingsMenu_ReflectsPersistedPreferences()
    {
        var window = Create();
        window.Show();
        Assert.True(window.FindControl<MenuItem>("Time12")!.IsChecked);
        Assert.True(window.FindControl<MenuItem>("DateSystem")!.IsChecked);
        Assert.False(window.FindControl<MenuItem>("DateMonth")!.IsChecked);
        Assert.False(window.FindControl<MenuItem>("MenuUntrusted")!.IsChecked);
        window.Close();
    }

    [AvaloniaFact]
    public void ResultsView_AddsRowNumberColumn_AndLoadsRows()
    {
        var series = new Tickbird.Core.Data.ResultSeries("m", new List<string> { "time", "v" },
            new Dictionary<string, string> { ["host"] = "a" },
            new List<IList<object>> { new List<object> { "2024-01-02T03:04:05Z", 1 }, new List<object> { "2024-01-02T03:04:06Z", 2 } });
        Create();
        var view = new ResultsView();
        var host = new Window { Content = view };
        host.Show();
        Assert.Equal(2, view.Show(series));
        var grid = view.GetVisualDescendants().OfType<DataGrid>().Single();
        Assert.Equal(3, grid.Columns.Count);
        Assert.Equal("#", grid.Columns[0].Header);
        host.Close();
    }

    [AvaloniaFact]
    public async Task DoubleClickOnMeasurement_OpensNewQueryForIt()
    {
        var window = Create();
        window.Show();
        var vm = (MainWindowViewModel)window.DataContext!;
        var connection = new Tickbird.Core.Data.DatabaseConnection("1", "Local", "127.0.0.1", 1, null, null);
        var server = new TreeNodeViewModel(connection.Name, Tickbird.Core.Navigation.NodeKind.Connection, null, connection);
        var db = new TreeNodeViewModel("telemetry", Tickbird.Core.Navigation.NodeKind.Database, server);
        var measurement = new TreeNodeViewModel("gps", Tickbird.Core.Navigation.NodeKind.Measurement, db);
        db.Children.Add(measurement);
        server.Children.Add(db);
        vm.Nodes.Add(server);

        await window.OpenNodeAsync(measurement);

        var tab = Assert.Single(vm.Tabs);
        Assert.Equal("Local.telemetry.gps", tab.Title);
        var query = Assert.IsType<QueryView>(tab.Content);
        Assert.Contains("FROM \"gps\"", query.EditorText);
        window.Close();
    }

    [AvaloniaFact]
    public void ResultsGrid_ColumnsAreResizable_AndSizedToContent()
    {
        Create();
        var series = new Tickbird.Core.Data.ResultSeries("m", new List<string> { "time", "v", "a_long_column_name_for_status" },
            new Dictionary<string, string>(),
            new List<IList<object>> { new List<object> { "2024-01-02T03:04:05Z", 1, "ok" } });
        var view = new ResultsView();
        var host = new Window { Content = view, Width = 900, Height = 400 };
        host.Show();
        view.Show(series);
        var grid = view.ResultsGrid;
        Assert.True(grid.CanUserResizeColumns);
        Assert.All(grid.Columns, c => Assert.True(c.CanUserResize));
        // Pixel widths (not star): dragging one column no longer squeezes the others.
        Assert.All(grid.Columns, c => Assert.True(c.Width.IsAbsolute));
        Assert.True(grid.Columns[3].Width.Value > grid.Columns[2].Width.Value);   // long header gets more room

        grid.Columns[2].Width = new DataGridLength(300);
        view.AutoSizeColumns();
        Assert.True(grid.Columns[2].Width.Value < 300);
        host.Close();
    }

    [AvaloniaFact]
    public void FormDialog_Opens()
    {
        var dialog = new FormDialog("T", "OK", ("Name:", Ui.Text()));
        dialog.Show();
        Assert.True(dialog.IsVisible);
        dialog.Close();
    }
}
