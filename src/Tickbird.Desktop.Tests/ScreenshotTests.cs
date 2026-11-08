using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Tickbird.Core.Data;
using Tickbird.Core.Navigation;
using Tickbird.Core.Settings;
using Tickbird.Desktop.Services;
using Tickbird.Desktop.ViewModels;
using Tickbird.Desktop.Views;
using Tickbird.Desktop.Views.Requests;

namespace Tickbird.Desktop.Tests;

/// <summary>Renders the populated main window to a PNG so the layout can be reviewed. Set INFLUX_SCREENSHOT_DIR to enable.</summary>
public class ScreenshotTests
{
    [AvaloniaFact]
    public async Task RenderMainWindow()
    {
        var target = Environment.GetEnvironmentVariable("INFLUX_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(target)) return;

        var app = new TickbirdApp(new SettingsStore(Path.Combine(Path.GetTempPath(), "influx-shot-" + Guid.NewGuid().ToString("N"), "s.json")));
        TickbirdApp.Current = app;
        var window = new MainWindow(app, showConnectionsOnStart: false) { Width = 1100, Height = 700 };
        var vm = (MainWindowViewModel)window.DataContext!;

        var connection = new DatabaseConnection("1", "Local InfluxDB", "localhost", 8086, "u", "p");
        var node = new TreeNodeViewModel(connection.Name, NodeKind.Connection, null, connection);
        var db = new TreeNodeViewModel("telegraf", NodeKind.Database, node);
        db.Children.Add(new TreeNodeViewModel("cpu", NodeKind.Measurement, db));
        db.Children.Add(new TreeNodeViewModel("mem", NodeKind.Measurement, db));
        node.Children.Add(db);
        node.IsExpanded = true;
        db.IsExpanded = true;
        vm.Nodes.Add(node);

        var query = new QueryView { Client = null!, Database = "telegraf", EditorText = "SELECT mean(\"usage\") FROM \"cpu\" WHERE time > now() - 5m GROUP BY time(1m) -- sample" };
        vm.AddTab(new TabViewModel("q", "Local InfluxDB.telegraf", TabKind.Query, "1", "RunQuery", query));
        var results = new ResultsView();
        results.Show(new ResultSeries("cpu", new List<string> { "time", "host", "usage" }, new Dictionary<string, string>(),
            new List<IList<object>>
            {
                new List<object> { "2024-01-02T03:04:05Z", "web-1", 41.5 },
                new List<object> { "2024-01-02T03:05:05Z", "web-2", 12.25 },
            }));
        vm.AddTab(new TabViewModel("r", "Local InfluxDB.cpu.field_keys", TabKind.Request, "1", "FieldKeys", results));
        vm.SelectedNode = db;
        window.Show();
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        Directory.CreateDirectory(target);
        frame?.Save(Path.Combine(target, "main-window.png"));
        vm.Status = "Ready";
        vm.SelectedTab = vm.Tabs[0];
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(target, "query-tab.png"));
        var tree = window.FindControl<TreeView>("Tree")!;
        var connectionRow = (TreeViewItem)tree.ContainerFromIndex(0)!;
        var databaseRow = (TreeViewItem)connectionRow.ContainerFromIndex(0)!;
        var menuPosition = databaseRow.TranslatePoint(new Point(100, 16), window)!.Value;
        window.MouseDown(menuPosition, MouseButton.Right);
        window.MouseUp(menuPosition, MouseButton.Right);
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        tree.ContextMenu!.UpdateLayout();
        TopLevel.GetTopLevel(tree.ContextMenu)?.CaptureRenderedFrame()?.Save(Path.Combine(target, "database-menu.png"));
        tree.ContextMenu.Close();
        window.Close();

        app.Settings.Connections.Add(connection);
        var manage = new ManageConnectionsWindow(app) { Width = 960, Height = 300 };
        manage.Show();
        manage.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        manage.CaptureRenderedFrame()?.Save(Path.Combine(target, "manage-connections.png"));
        var confirmation = Dialogs.ConfirmAsync(manage, "Confirm Delete", "Are you sure you want to delete the connection \"Local InfluxDB\"?");
        var confirmWindow = Assert.Single(manage.OwnedWindows);
        confirmWindow.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        confirmWindow.CaptureRenderedFrame()?.Save(Path.Combine(target, "confirm-delete.png"));
        confirmWindow.Close(false);
        await confirmation;
        manage.Close();
    }
}
