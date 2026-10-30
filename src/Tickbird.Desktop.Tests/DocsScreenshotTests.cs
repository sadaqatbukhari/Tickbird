using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Tickbird.Core.Data;
using Tickbird.Core.Mapping;
using Tickbird.Core.Navigation;
using Tickbird.Core.Settings;
using Tickbird.Desktop.Services;
using Tickbird.Desktop.ViewModels;
using Tickbird.Desktop.Views;
using Tickbird.Desktop.Views.Requests;

namespace Tickbird.Desktop.Tests;

/// <summary>
/// Renders the screenshots used by README.md from sample data (no server needed).
/// Set TICKBIRD_DOCS_IMG_DIR (for example to docs/img) to enable; the test does nothing otherwise.
/// </summary>
public class DocsScreenshotTests
{
    private static string? Target => Environment.GetEnvironmentVariable("TICKBIRD_DOCS_IMG_DIR");

    private static void Pump()
    {
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
    }

    private static void Save(Window window, string name)
    {
        window.UpdateLayout();
        Pump();
        window.UpdateLayout();
        Pump();
        Directory.CreateDirectory(Target!);
        window.CaptureRenderedFrame()?.Save(Path.Combine(Target!, name));
    }

    private static TickbirdApp NewApp()
    {
        var app = new TickbirdApp(new SettingsStore(Path.Combine(Path.GetTempPath(), "tickbird-docs-" + Guid.NewGuid().ToString("N"), "settings.json")));
        TickbirdApp.Current = app;
        MapTiles.Enabled = false;   // no network; the track is still drawn
        return app;
    }

    private static DateTime T(int minute) => new(2026, 10, 21, 7, minute, 0, DateTimeKind.Utc);

    private static ResultSeries Cpu() =>
        new("cpu", new List<string> { "time", "host", "usage_user", "usage_system" }, new Dictionary<string, string>(),
            Enumerable.Range(0, 48).Select(i => (IList<object>)new List<object>
            {
                T(i), "web-1", Math.Round(35 + Math.Sin(i / 5.0) * 12 + (i % 7), 2), Math.Round(12 + Math.Cos(i / 6.0) * 4, 2)
            }).ToList());

    private static ResultSeries Gps() =>
        new("gps", new List<string> { "time", "latitude", "longitude", "state", "speed" }, new Dictionary<string, string>(),
            Enumerable.Range(0, 120).Select(i => (IList<object>)new List<object>
            {
                new DateTime(2026, 10, 21, 7, 0, 0, DateTimeKind.Utc).AddSeconds(i * 5),
                Math.Round(52.2300 + Math.Sin(i / 12.0) * 0.004 + i * 0.00004, 5),
                Math.Round(21.0100 + Math.Cos(i / 12.0) * 0.006 + i * 0.00008, 5),
                i % 30 < 20 ? "work" : "turn",
                Math.Round(2 + (i % 30 < 20 ? 3 : 0) + Math.Abs(Math.Sin(i / 7.0)) * 2, 1)
            }).ToList());

    private static TreeNodeViewModel AddServer(MainWindowViewModel vm, string id, string name, string? group, params string[] databases)
    {
        var connection = new DatabaseConnection(id, name, "localhost", 8086, "u", "p") { Group = group };
        var server = new TreeNodeViewModel(name, NodeKind.Connection, null, connection);
        foreach (var database in databases)
        {
            var db = new TreeNodeViewModel(database, NodeKind.Database, server);
            db.Children.Add(new TreeNodeViewModel("cpu", NodeKind.Measurement, db));
            db.Children.Add(new TreeNodeViewModel("gps", NodeKind.Measurement, db));
            db.Children.Add(new TreeNodeViewModel("mem", NodeKind.Measurement, db));
            server.Children.Add(db);
        }
        vm.AddConnectionNode(server);
        server.IsExpanded = true;
        return server;
    }

    private static (MainWindow Window, MainWindowViewModel Vm, TreeNodeViewModel Telemetry) MainWindowWithTree(TickbirdApp app)
    {
        var window = new MainWindow(app, showConnectionsOnStart: false) { Width = 1180, Height = 760 };
        var vm = (MainWindowViewModel)window.DataContext!;
        var prod = AddServer(vm, "1", "Production", "PROD", "telemetry", "ems");
        AddServer(vm, "2", "Staging", "DEV", "telemetry");
        AddServer(vm, "3", "Local InfluxDB", null, "telegraf");
        var telemetry = prod.Children[0];
        telemetry.IsExpanded = true;
        return (window, vm, telemetry);
    }

    private static void AddResultsTab(MainWindowViewModel vm, ResultsView results, string title, string id)
    {
        var tab = new TabViewModel(id, title, TabKind.Request, "1", "RunQuery", results);
        vm.AddTab(tab);
        vm.SelectedTab = tab;
    }

    [AvaloniaFact]
    public void MainWindow_WithGroupsAndQuery()
    {
        if (string.IsNullOrEmpty(Target)) return;
        var app = NewApp();
        var (window, vm, telemetry) = MainWindowWithTree(app);
        var query = new QueryView
        {
            Client = null!, Database = "telemetry",
            EditorText = "SELECT mean(\"usage_user\") AS cpu\nFROM \"cpu\"\nWHERE \"host\" = 'web-1' AND time > now() - 1h\nGROUP BY time(1m)"
        };
        vm.AddTab(new TabViewModel("q", "Production.telemetry", TabKind.Query, "1", "RunQuery", query));
        vm.SelectedNode = telemetry;
        window.Show();
        Pump();
        Save(window, "AppForm_InitialView.png");

        var results = new ResultsView();
        results.Show(Cpu());
        AddResultsTab(vm, results, "Production.telemetry.cpu", "r");
        Save(window, "Databases_RunQuery_2.png");

        results.SetMode(ResultMode.Chart);
        Save(window, "Results_Chart.png");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Results_Chart_Map_AndFilter()
    {
        if (string.IsNullOrEmpty(Target)) return;
        var app = NewApp();

        var chart = new ResultsView { ChartSettings = new ChartSettings { Mode = ResultMode.Chart } };
        var host = new Window { Content = chart, Width = 1000, Height = 520 };
        host.Show();
        chart.Show(Cpu());
        await chart.Chart!.RefreshAsync();
        host.Close();

        var map = new ResultsView { ChartSettings = new ChartSettings { Mode = ResultMode.Map } };
        host = new Window { Content = map, Width = 1000, Height = 560 };
        host.Show();
        map.Show(Gps());
        await map.Map!.RefreshAsync();
        Save(host, "Results_Map.png");
        host.Close();

        var grid = new ResultsView();
        host = new Window { Content = grid, Width = 1000, Height = 420 };
        host.Show();
        grid.Show(Gps());
        grid.AddFilterCondition("state", MapConditionOperator.Equals, "work");
        Save(host, "Results_Filter.png");
        host.Close();
        _ = app;
    }

    [AvaloniaFact]
    public async Task ConnectionWindows()
    {
        if (string.IsNullOrEmpty(Target)) return;
        var app = NewApp();

        var blank = new ManageConnectionsWindow(app) { Width = 1100, Height = 360 };
        blank.Show();
        Save(blank, "ManageConnectionsDialog_Blank.png");
        blank.Close();

        app.Settings.Groups.AddRange(new[] { "PROD", "DEV" });
        app.Settings.Connections.Add(new DatabaseConnection("1", "Production", "influx.example.com", 8086, "admin", "x", true, "telemetry") { Group = "PROD" });
        app.Settings.Connections.Add(new DatabaseConnection("2", "Staging", "staging.example.com", 8181, null, null, true, "telemetry") { Group = "DEV" });
        app.Settings.Connections.Add(new DatabaseConnection("3", "Local InfluxDB", "localhost", 8086, null, null));
        var manage = new ManageConnectionsWindow(app) { Width = 1100, Height = 360 };
        manage.Show();
        Save(manage, "ManageConnectionsDialog_WithLocalhost.png");
        manage.Close();

        var groups = new ManageGroupsWindow(app.Settings, () => { }) { };
        groups.Show();
        Save(groups, "Connections_Groups.png");
        groups.Close();

        var owner = new Window { Width = 900, Height = 700 };
        owner.Show();
        var existing = new DatabaseConnection("1", "Production", "influx.example.com", 8086, "admin", "secret", true, "telemetry") { Group = "PROD" };
        var pending = TickbirdDialogs.ConnectionAsync(owner, existing, app.Settings);
        if (pending.IsFaulted) await pending;
        var dialog = Assert.Single(owner.OwnedWindows);
        Save(dialog, "ConnectionsDialog_1.png");
        dialog.Close();
        owner.Close();
    }

    [AvaloniaFact]
    public void DropConfirmation()
    {
        if (string.IsNullOrEmpty(Target)) return;
        NewApp();
        var owner = new Window { Width = 700, Height = 400 };
        owner.Show();
        var dialog = new ConfirmByNameWindow("Drop Database",
            "Are you sure you want to drop the database \"telemetry\"? This is a permanent operation.", "telemetry", "Drop");
        _ = dialog.ShowDialog<bool>(owner);
        Pump();
        dialog.Input.Text = "telemetry";
        Save(dialog, "Databases_Drop_2.png");
        dialog.Close(false);
        owner.Close();
    }
}
