using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Headless.XUnit;
using Tickbird.Core.Data;
using Tickbird.Core.History;
using Tickbird.Core.Navigation;
using Tickbird.Core.Settings;
using Tickbird.Desktop.Services;
using Tickbird.Desktop.ViewModels;
using Tickbird.Desktop.Views;
using Tickbird.Desktop.Views.Requests;

namespace Tickbird.Desktop.Tests;

public sealed class ExplorerFilterAndHistoryTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "tickbird-filter-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    private (MainWindow Window, MainWindowViewModel Vm, TickbirdApp App) Create()
    {
        var app = new TickbirdApp(new SettingsStore(Path.Combine(dir, "settings.json")));
        TickbirdApp.Current = app;
        var window = new MainWindow(app, showConnectionsOnStart: false);
        window.Show();
        return (window, (MainWindowViewModel)window.DataContext!, app);
    }

    private static (TreeNodeViewModel Server, TreeNodeViewModel Telemetry, TreeNodeViewModel Gps, TreeNodeViewModel Cpu, TreeNodeViewModel Ems) Tree(MainWindowViewModel vm)
    {
        var connection = new DatabaseConnection("1", "Local", "127.0.0.1", 1, null, null);
        var server = new TreeNodeViewModel("Local", NodeKind.Connection, null, connection);
        var telemetry = new TreeNodeViewModel("telemetry", NodeKind.Database, server);
        var gps = new TreeNodeViewModel("gps_position", NodeKind.Measurement, telemetry);
        var cpu = new TreeNodeViewModel("cpu", NodeKind.Measurement, telemetry);
        var ems = new TreeNodeViewModel("ems", NodeKind.Database, server);
        telemetry.Children.Add(gps);
        telemetry.Children.Add(cpu);
        ems.Children.Add(new TreeNodeViewModel("battery", NodeKind.Measurement, ems));
        server.Children.Add(telemetry);
        server.Children.Add(ems);
        vm.Nodes.Add(server);
        return (server, telemetry, gps, cpu, ems);
    }

    [AvaloniaFact]
    public async Task Filter_ShowsMatchesAndTheirParents_AndClearingShowsAll()
    {
        var (window, vm, _) = Create();
        var t = Tree(vm);

        await window.ApplyTreeFilterAsync("GPS");
        Assert.True(t.Server.IsShown && t.Telemetry.IsShown && t.Gps.IsShown);
        Assert.False(t.Cpu.IsShown);
        Assert.False(t.Ems.IsShown);
        Assert.True(t.Telemetry.IsExpanded);
        Assert.Equal(1, vm.CountTreeMatches());

        // A matching database shows all of its measurements.
        await window.ApplyTreeFilterAsync("ems");
        Assert.True(t.Ems.IsShown);
        Assert.True(t.Ems.Children[0].IsShown);
        Assert.False(t.Telemetry.IsShown);

        await window.ApplyTreeFilterAsync("");
        Assert.True(t.Cpu.IsShown && t.Ems.IsShown && t.Telemetry.IsShown);
        window.Close();
    }

    [AvaloniaFact]
    public void QueryHistoryTab_ListsAndSearchesRunQueries()
    {
        var (window, vm, app) = Create();
        app.History.Add(new QueryHistoryEntry { ConnectionId = "1", ConnectionName = "Local", Database = "telemetry", Query = "SELECT * FROM gps", RunAtUtc = DateTime.UtcNow });
        app.History.Add(new QueryHistoryEntry { ConnectionId = "1", ConnectionName = "Local", Database = "telemetry", Query = "SHOW MEASUREMENTS", RunAtUtc = DateTime.UtcNow, Status = QueryRunStatus.Cancelled });

        window.ShowQueryHistory();
        window.ShowQueryHistory();   // reopening focuses the same tab
        var tab = Assert.Single(vm.Tabs);
        var view = Assert.IsType<QueryHistoryView>(tab.Content);
        Assert.False(tab.IsRequest);
        Assert.Equal(2, view.Shown.Count);
        view.SearchFor("gps");
        Assert.Single(view.Shown);
        window.Close();
    }

    /// <summary>Answers every HTTP request like InfluxDB 1.x answers /ping (204 + version header).</summary>
    private static TcpListener StartPingServer()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var client = await listener.AcceptTcpClientAsync();
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            var stream = client.GetStream();
                            var buffer = new byte[4096];
                            var request = new StringBuilder();
                            while (!request.ToString().Contains("\r\n\r\n"))
                            {
                                var read = await stream.ReadAsync(buffer);
                                if (read == 0) return;
                                request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                            }
                            var response = "HTTP/1.1 204 No Content\r\nX-Influxdb-Version: 1.8.10\r\nX-Influxdb-Build: OSS\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
                        }
                    });
                }
            }
            catch (Exception) { /* stopped */ }
        });
        return listener;
    }

    [AvaloniaFact]
    public async Task OpeningFromHistory_ConnectsTheServerFirst()
    {
        var server = StartPingServer();
        try
        {
            var (window, vm, app) = Create();
            var connection = new DatabaseConnection("srv1", "Fleet", "127.0.0.1", (ushort)((IPEndPoint)server.LocalEndpoint).Port, null, null, useSsl: false);
            app.Settings.Connections.Add(connection);
            Assert.Null(vm.FindConnectionNode("srv1"));

            await window.OpenFromHistoryAsync(new QueryHistoryEntry
            {
                ConnectionId = "srv1", ConnectionName = "Fleet", Database = "telemetry", Query = "SELECT * FROM gps", RunAtUtc = DateTime.UtcNow
            });

            Assert.NotNull(vm.FindConnectionNode("srv1"));          // now connected and in the Object Explorer
            Assert.True(app.IsConnected(connection));
            var tab = Assert.Single(vm.Tabs);
            var query = Assert.IsType<QueryView>(tab.Content);
            Assert.Equal("SELECT * FROM gps", query.EditorText);
            Assert.Equal("telemetry", query.Database);

            // A second entry on the same server reuses the connection.
            await window.OpenFromHistoryAsync(new QueryHistoryEntry { ConnectionId = "srv1", ConnectionName = "Fleet", Database = "telemetry", Query = "SHOW MEASUREMENTS" });
            Assert.Single(vm.ConnectionNodes());
            Assert.Equal(2, vm.Tabs.Count);
            window.Close();
        }
        finally
        {
            server.Stop();
        }
    }

    [AvaloniaFact]
    public void QueryEditor_FindAndReplace_AndStopWhenIdle()
    {
        var app = new TickbirdApp(new SettingsStore(Path.Combine(dir, "settings.json")));
        TickbirdApp.Current = app;
        var view = new QueryView { EditorText = "SELECT * FROM gps WHERE time > now() - 5m" };
        var host = new Avalonia.Controls.Window { Content = view, Width = 900, Height = 600 };
        host.Show();

        Assert.False(view.IsRunning);
        view.CancelQuery();   // nothing running: no effect
        view.Editor.Select(14, 3);
        view.ShowFind(replace: true);
        Assert.True(view.Search.IsReplaceMode);
        Assert.Equal("gps", view.Search.SearchPattern);
        view.ShowFind(replace: false);
        Assert.False(view.Search.IsReplaceMode);
        host.Close();
    }
}
