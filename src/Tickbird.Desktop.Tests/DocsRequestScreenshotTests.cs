using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Tickbird.Core.Data;
using Tickbird.Core.Navigation;
using Tickbird.Core.Settings;
using Tickbird.Desktop.Services;
using Tickbird.Desktop.ViewModels;
using Tickbird.Desktop.Views;
using Tickbird.Desktop.Views.Requests;

namespace Tickbird.Desktop.Tests;

/// <summary>An in-memory server with sample data, so the real views can be rendered for the documentation.</summary>
internal sealed class SampleClient : DatabaseClient
{
    public SampleClient() : base(new DatabaseConnection("1", "Sample", "localhost", 8086, null, null)) { }

    private static Task<T> Done<T>(T value) => Task.FromResult(value);
    private static Task<CommandResult> Ok() => Done(new CommandResult("{}", HttpStatusCode.OK, true));

    public override Task<IEnumerable<string>> GetDatabaseNamesAsync() => Done<IEnumerable<string>>(new[] { "telemetry", "ems" });
    public override Task<CommandResult> CreateDatabaseAsync(string database) => Ok();
    public override Task<CommandResult> DropDatabaseAsync(string database) => Ok();
    public override Task<IEnumerable<RetentionPolicy>> GetRetentionPoliciesAsync(string database) =>
        Done<IEnumerable<RetentionPolicy>>(new[] { new RetentionPolicy { Name = "autogen", Database = database, Duration = "0s", ShardGroupDuration = "168h0m0s", ReplicationCopies = 1, Default = true } });
    public override Task<CommandResult> CreateRetentionPolicyAsync(string database, string policyName, string duration, int replication, bool isDefault = false) => Ok();
    public override Task<CommandResult> AlterRetentionPolicyAsync(string database, string policyName, string duration, int replication, bool isDefault = false) => Ok();
    public override Task<CommandResult> DropRetentionPolicyAsync(string database, string policyName) => Ok();
    public override Task<IEnumerable<string>> GetMeasurementNamesAsync(string database) => Done<IEnumerable<string>>(new[] { "cpu", "gps", "mem" });
    public override Task<CommandResult> DropMeasurementAsync(string database, string measurement) => Ok();
    public override Task<IEnumerable<string>> GetTagKeysAsync(string database, string measurement) => Done<IEnumerable<string>>(new[] { "host", "region" });
    public override Task<IEnumerable<TagValue>> GetTagValuesAsync(string database, string measurement, string tag) =>
        Done<IEnumerable<TagValue>>(new[] { "db-1", "web-1", "web-2", "web-3" }.Select(v => new TagValue(tag, v)).ToList());
    public override Task<IEnumerable<FieldKey>> GetFieldKeysAsync(string database, string measurement) =>
        Done<IEnumerable<FieldKey>>(new[] { new FieldKey("usage_idle", "float"), new FieldKey("usage_system", "float"), new FieldKey("usage_user", "float"), new FieldKey("cores", "integer") });
    public override Task<IEnumerable<string>> GetSeriesNamesAsync(string database, string measurement = null) =>
        Done<IEnumerable<string>>(new[] { "cpu,host=db-1,region=eu", "cpu,host=web-1,region=eu", "cpu,host=web-2,region=us", "cpu,host=web-3,region=us" }.Select(s => s.Replace("cpu", measurement ?? "cpu")).ToList());
    public override Task<CommandResult> DropSeriesAsync(string database, string measurement = null) => Ok();
    public override Task<IEnumerable<RunningQuery>> GetRunningQueriesAsync() => Done<IEnumerable<RunningQuery>>(Array.Empty<RunningQuery>());
    public override Task<CommandResult> KillQueryAsync(int pid) => Ok();

    private static DateTime T(int minute) => new(2026, 10, 21, 7, minute, 0, DateTimeKind.Utc);

    private static ResultSeries Cpu(string host, double offset, IDictionary<string, string>? tags = null) =>
        new("cpu", new List<string> { "time", "mean_usage" }, tags != null ? new Dictionary<string, string>(tags) : new Dictionary<string, string>(),
            Enumerable.Range(0, 20).Select(i => (IList<object>)new List<object> { T(i * 3), Math.Round(offset + Math.Sin(i / 3.0) * 8, 2) }).ToList());

    public override Task<IEnumerable<ResultSeries>> QueryAsync(string database, string query)
    {
        var grouped = query.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase) && query.Contains("host", StringComparison.OrdinalIgnoreCase)
                      && query.IndexOf("host", query.IndexOf("GROUP BY", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase) > 0;
        IEnumerable<ResultSeries> result = grouped
            ? new[] { "web-1", "web-2", "web-3" }.Select((h, i) => Cpu(h, 30 + i * 12, new Dictionary<string, string> { ["host"] = h })).ToList()
            : new[] { Cpu("web-1", 38) };
        return Done(result);
    }

    public override Task<IEnumerable<ContinuousQuery>> GetContinousQueriesAsync(string database) =>
        Done<IEnumerable<ContinuousQuery>>(new[]
        {
            new ContinuousQuery("cq_cpu_1h",
                "CREATE CONTINUOUS QUERY \"cq_cpu_1h\" ON \"" + database + "\"\nBEGIN\n  SELECT mean(\"usage_user\") AS \"usage_user\"\n  INTO \"" + database + "\".\"autogen\".\"cpu_1h\"\n  FROM \"" + database + "\".\"autogen\".\"cpu\"\n  GROUP BY time(1h), \"host\"\nEND"),
            new ContinuousQuery("cq_mem_10m", "CREATE CONTINUOUS QUERY \"cq_mem_10m\" ON \"" + database + "\" BEGIN SELECT max(\"used\") INTO \"mem_10m\" FROM \"mem\" GROUP BY time(10m) END")
        });
    public override Task<CommandResult> CreateContinuousQueryAsync(ContinuousQueryParams cqParams) => Ok();
    public override Task<CommandResult> DropContinuousQueryAsync(string database, string cqName) => Ok();
    public override Task<CommandResult> BackfillAsync(string database, BackfillParams backfillParams) => Ok();
    public override Task<CommandResult> WriteAsync(string database, string measurement, IDictionary<string, object> tags, IDictionary<string, object> fields, DateTime timeStamp, string retentionPolicy = null) => Ok();
    public override Task<CommandResult> WriteAsync(string database, DataPoint point, string retentionPolicy = null) => Ok();
    public override Task<CommandResult> WriteAsync(string database, IEnumerable<DataPoint> points, string retentionPolicy = null) => Ok();
    public override Task<PingResponse> PingAsync() => Done(new PingResponse(true, TimeSpan.FromMilliseconds(12), "1.8.10"));
    public override Task<ServerDiagnostics> GetDiagnosticsAsync() => Done(new ServerDiagnostics
    {
        Hostname = "influx-prod-01", Branch = "1.8", Commit = "688e697c51fd", BuildVersion = "1.8.10",
        Started = new DateTime(2026, 10, 14, 5, 12, 0, DateTimeKind.Utc), CurrentTime = new DateTime(2026, 10, 21, 7, 30, 0, DateTimeKind.Utc),
        Uptime = new TimeSpan(7, 2, 18, 0), PID = 2214, GoArch = "amd64", GoMaxProc = 8, GoOs = "linux", GoVersion = "go1.13.8"
    });
    public override Task<ServerStats> GetStatsAsync() => Done(new ServerStats());
    public override Task<IEnumerable<DatabaseUser>> GetUsersAsync() =>
        Done<IEnumerable<DatabaseUser>>(new[] { new DatabaseUser("admin", true), new DatabaseUser("grafana", false), new DatabaseUser("telegraf", false), new DatabaseUser("viewer", false) });
    public override Task<CommandResult> CreateUserAsync(string username, string password, bool isAdmin) => Ok();
    public override Task<CommandResult> DropUserAsync(string username) => Ok();
    public override Task<CommandResult> SetPasswordAsync(string username, string password) => Ok();
    public override Task<IEnumerable<DatabaseGrant>> GetPrivilegesAsync(string username) =>
        Done<IEnumerable<DatabaseGrant>>(new[] { new DatabaseGrant("telemetry", DatabasePrivileges.All), new DatabaseGrant("ems", DatabasePrivileges.Read) });
    public override Task<CommandResult> GrantAdministratorAsync(string username) => Ok();
    public override Task<CommandResult> RevokeAdministratorAsync(string username) => Ok();
    public override Task<CommandResult> GrantPrivilegeAsync(string username, DatabasePrivileges privilege, string database) => Ok();
    public override Task<CommandResult> RevokePrivilegeAsync(string username, DatabasePrivileges privilege, string database) => Ok();
}

/// <summary>
/// Renders the remaining README screenshots (menus, dialogs and the request tabs) from a <see cref="SampleClient"/>.
/// Enabled by TICKBIRD_DOCS_IMG_DIR, like <see cref="DocsScreenshotTests"/>.
/// </summary>
public class DocsRequestScreenshotTests
{
    private static string? Target => Environment.GetEnvironmentVariable("TICKBIRD_DOCS_IMG_DIR");

    private static void Pump()
    {
        for (var i = 0; i < 6; i++) Dispatcher.UIThread.RunJobs();
    }

    private static void Save(TopLevel window, string name)
    {
        (window as Window)?.UpdateLayout();
        Pump();
        Directory.CreateDirectory(Target!);
        window.CaptureRenderedFrame()?.Save(Path.Combine(Target!, name));
    }

    private static (MainWindow Window, MainWindowViewModel Vm, TreeNodeViewModel Server, TreeNodeViewModel Db, TreeNodeViewModel Measurement) Create()
    {
        var app = new TickbirdApp(new SettingsStore(Path.Combine(Path.GetTempPath(), "tickbird-docs-" + Guid.NewGuid().ToString("N"), "settings.json")));
        TickbirdApp.Current = app;
        var window = new MainWindow(app, showConnectionsOnStart: false) { Width = 1180, Height = 760 };
        var vm = (MainWindowViewModel)window.DataContext!;
        var connection = new DatabaseConnection("1", "Local InfluxDB", "localhost", 8086, "u", "p");
        var server = new TreeNodeViewModel(connection.Name, NodeKind.Connection, null, connection);
        TreeNodeViewModel? db = null, measurement = null;
        foreach (var name in new[] { "telemetry", "ems" })
        {
            var node = new TreeNodeViewModel(name, NodeKind.Database, server);
            foreach (var m in new[] { "cpu", "gps", "mem" })
            {
                var child = new TreeNodeViewModel(m, NodeKind.Measurement, node);
                node.Children.Add(child);
                if (name == "telemetry" && m == "cpu") measurement = child;
            }
            server.Children.Add(node);
            db ??= node;
        }
        vm.Nodes.Add(server);
        server.IsExpanded = true;
        db!.IsExpanded = true;
        window.Show();
        Pump();
        return (window, vm, server, db, measurement!);
    }

    private static async Task ShowRequest(MainWindow window, MainWindowViewModel vm, RequestView view, string title, string name)
    {
        view.Client = new SampleClient();
        view.Database = "telemetry";
        vm.AddTab(new TabViewModel(name, title, TabKind.Request, "1", "Show", view));
        await view.ExecuteAsync();
        Pump();
        Save(window, name + ".png");
    }

    private static async Task Dialog(Window owner, Task pending, string name)
    {
        if (pending.IsFaulted) await pending;
        var dialog = Assert.Single(owner.OwnedWindows);
        Save(dialog, name + ".png");
        dialog.Close();
    }

    private static void SaveMenu(ContextMenu menu, Control anchor, string name)
    {
        menu.Open(anchor);
        Pump();
        menu.UpdateLayout();
        if (TopLevel.GetTopLevel(menu) is { } popup) Save(popup, name + ".png");
        menu.Close();
    }

    private static void RightClickMenu(Window window, TreeView tree, TreeViewItem row, string name)
    {
        var position = row.TranslatePoint(new Point(100, 16), window)!.Value;
        window.MouseDown(position, MouseButton.Right);
        window.MouseUp(position, MouseButton.Right);
        window.UpdateLayout();
        Pump();
        tree.ContextMenu!.UpdateLayout();
        if (TopLevel.GetTopLevel(tree.ContextMenu) is { } popup) Save(popup, name + ".png");
        tree.ContextMenu.Close();
        Pump();
    }

    [AvaloniaFact]
    public async Task MeasurementViews()
    {
        if (string.IsNullOrEmpty(Target)) return;
        var (window, vm, _, db, cpu) = Create();
        vm.SelectedNode = cpu;
        await ShowRequest(window, vm, new TagKeysView { Measurement = "cpu" }, "Local InfluxDB.cpu.tag_keys", "Measurements_ShowTagKeys");
        await ShowRequest(window, vm, new TagValuesView { Measurement = "cpu" }, "Local InfluxDB.cpu.tag_values", "Measurements_ShowTagValues");
        await ShowRequest(window, vm, new FieldKeysView { Measurement = "cpu" }, "Local InfluxDB.cpu.field_keys", "Measurements_ShowFieldKeys");
        await ShowRequest(window, vm, new SeriesView { Measurement = "cpu" }, "Local InfluxDB.cpu.series", "Measurements_ShowSeries");
        _ = db;
        window.Close();
    }

    [AvaloniaFact]
    public async Task QueryTabs_AndExport()
    {
        if (string.IsNullOrEmpty(Target)) return;
        var (window, vm, _, db, cpu) = Create();
        vm.SelectedNode = db;

        async Task<QueryView> Run(string sql, string title, string file)
        {
            var query = new QueryView { Client = new SampleClient(), Database = "telemetry", EditorText = sql };
            var tab = new TabViewModel(file, title, TabKind.Query, "1", "RunQuery", query);
            vm.AddTab(tab);
            vm.SelectedTab = tab;
            window.UpdateLayout();
            Pump();
            await query.ExecuteAsync();
            Pump();
            Save(window, file + ".png");
            return query;
        }

        await Run("SELECT mean(\"usage_user\") FROM \"cpu\" WHERE time > now() - 1h GROUP BY time(3m)", "Local InfluxDB.telemetry", "Databases_RunQuery_1");
        var grouped = await Run("SELECT mean(\"usage_user\") FROM \"cpu\" WHERE time > now() - 1h GROUP BY time(3m), \"host\"", "Local InfluxDB.telemetry (2)", "Databases_RunQuery_3");
        File.Copy(Path.Combine(Target!, "Databases_RunQuery_1.png"), Path.Combine(Target!, "Measurements_RunQuery_1.png"), true);

        var results = grouped.GetVisualDescendantsOf<ResultsView>().FirstOrDefault();
        if (results?.ResultsGrid.ContextMenu is { } menu) SaveMenu(menu, results.ResultsGrid, "Databases_ExportQueryResults");
        _ = cpu;
        window.Close();
    }

    [AvaloniaFact]
    public async Task AdminViews_AndTreeMenus()
    {
        if (string.IsNullOrEmpty(Target)) return;
        var (window, vm, server, db, cpu) = Create();
        vm.SelectedNode = server;

        var tree = window.FindControl<TreeView>("Tree")!;
        var connectionRow = (TreeViewItem)tree.ContainerFromIndex(0)!;
        var databaseRow = (TreeViewItem)connectionRow.ContainerFromIndex(0)!;
        RightClickMenu(window, tree, connectionRow, "Connections_ContextMenu");
        RightClickMenu(window, tree, databaseRow, "Databases_Drop_1");

        vm.SelectedNode = server;
        await ShowRequest(window, vm, new DiagnosticsView(), "Local InfluxDB.diagnostics", "Connections_Diagnostics_2");
        File.Copy(Path.Combine(Target!, "Connections_Diagnostics_2.png"), Path.Combine(Target!, "Connections_Diagnostics_1.png"), true);

        var users = new UsersView();
        await ShowRequest(window, vm, users, "Local InfluxDB.users", "Connections_ShowUsers");
        var usersGrid = users.GetVisualDescendantsOf<DataGrid>().First();
        usersGrid.SelectedIndex = 2;
        Pump();
        await Task.Delay(100);
        Pump();
        Save(window, "Connections_GrantUserPrivilege_2.png");
        File.Copy(Path.Combine(Target!, "Connections_ShowUsers.png"), Path.Combine(Target!, "Connections_CreateUser_2.png"), true);

        var cq = new ContinuousQueryView();
        await ShowRequest(window, vm, cq, "Local InfluxDB.telemetry.continuous_queries", "Databases_CQ_2");
        File.Copy(Path.Combine(Target!, "Databases_CQ_2.png"), Path.Combine(Target!, "Databases_CQ_1.png"), true);
        _ = cpu;
        window.Close();
    }

    [AvaloniaFact]
    public async Task DialogScreens()
    {
        if (string.IsNullOrEmpty(Target)) return;
        var (window, vm, _, _, _) = Create();
        var client = new SampleClient();
        var user = new DatabaseUser("telegraf", false);

        await Dialog(window, TickbirdDialogs.CreateDatabaseAsync(window, "Local InfluxDB", null, new[] { "telemetry", "ems" }), "Connections_CreateDatabase_1");
        await Dialog(window, TickbirdDialogs.CreateUserAsync(window), "Connections_CreateUser_1");
        await Dialog(window, TickbirdDialogs.EditUserAsync(window, user), "Connections_EditUser");
        await Dialog(window, TickbirdDialogs.PasswordAsync(window, user), "Connections_ChangeUserPassword");
        await Dialog(window, TickbirdDialogs.GrantPrivilegeAsync(window, user, new[] { "telemetry", "ems" }), "Connections_GrantUserPrivilege_1");
        await Dialog(window, TickbirdDialogs.EditPrivilegeAsync(window, "telegraf", new DatabaseGrant("telemetry", DatabasePrivileges.All)), "Connections_EditUserPrivilege");
        await Dialog(window, TickbirdDialogs.CreateContinuousQueryAsync(window, client, "telemetry"), "Databases_CQ_3");
        await Dialog(window, TickbirdDialogs.BackfillAsync(window, client, "telemetry"), "Databases_Backfill_1");
        await Dialog(window, Dialogs.ConfirmAsync(window, "Confirm Drop User", "Are you sure you want to drop the user \"telegraf\"? This is a permanent operation."), "Connections_DropUser");
        await Dialog(window, Dialogs.ConfirmAsync(window, "Confirm Drop Continuous Query", "Are you sure you want to drop the continuous query \"cq_cpu_1h\"?"), "Databases_DropCQ_1");
        await Dialog(window, Dialogs.ConfirmByNameAsync(window, "Drop Measurement",
            "Are you sure you want to drop the measurement \"cpu\"? This is a permanent operation.", "cpu"), "Measurements_DropMeasurement");
        await Dialog(window, Dialogs.ConfirmByNameAsync(window, "Drop Series",
            "Are you sure you want to drop all series of the measurement \"cpu\"? The measurement is kept.", "cpu"), "Measurements_DropSeries");

        vm.SelectedNode = vm.Nodes[0].Children[0];
        Save(window, "Connections_CreateDatabase_2.png");
        window.Close();
    }
}

internal static class VisualExtensions
{
    public static IEnumerable<T> GetVisualDescendantsOf<T>(this Visual root) where T : class =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<T>();
}
