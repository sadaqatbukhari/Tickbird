using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Tickbird.Core.Data;
using Tickbird.Core.Navigation;
using Tickbird.Core.Providers;
using Tickbird.Core.Settings;
using Tickbird.Desktop.Services;
using Tickbird.Desktop.ViewModels;
using Tickbird.Desktop.Views;

namespace Tickbird.Desktop.Tests;

/// <summary>The toolbar and the tree menu follow the database of the selected connection.</summary>
public sealed class CrateDbToolbarTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "tickbird-crate-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    private MainWindow Create()
    {
        var app = new TickbirdApp(new SettingsStore(Path.Combine(dir, "settings.json")));
        TickbirdApp.Current = app;
        var window = new MainWindow(app, showConnectionsOnStart: false);
        window.Show();
        return window;
    }

    private static TreeNodeViewModel AddServer(MainWindowViewModel vm, DatabaseKind kind, string name)
    {
        var connection = new DatabaseConnection(name, name, "localhost", kind == DatabaseKind.CrateDb ? (ushort)5432 : (ushort)8086, "u", "p") { Kind = kind };
        var server = new TreeNodeViewModel(name, NodeKind.Connection, null, connection);
        vm.AddConnectionNode(server);
        return server;
    }

    private static Dictionary<string, Button> Buttons(MainWindow window) =>
        window.FindControl<StackPanel>("ToolBar")!.Children.OfType<Button>().Where(b => b.Tag is string).ToDictionary(b => (string)b.Tag!);

    private static string? Tip(Button button) => ToolTip.GetTip(button) as string;

    private static string[] Visible(MainWindow window) =>
        Buttons(window).Where(b => b.Value.IsVisible).Select(b => b.Key).ToArray();

    [AvaloniaFact]
    public void WithNothingSelected_EveryCommandIsShown()
    {
        var window = Create();
        try
        {
            var buttons = Buttons(window);
            Assert.All(MainWindow.ToolbarCapabilities.Keys, icon => Assert.True(buttons[icon].IsVisible, icon));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SelectingCrateDb_HidesWhatItCannotDo()
    {
        var window = Create();
        try
        {
            var vm = (MainWindowViewModel)window.DataContext!;
            vm.SelectedNode = AddServer(vm, DatabaseKind.CrateDb, "crate-1");

            var shown = Visible(window);
            foreach (var icon in new[] { "RetentionPolicy", "CreateDatabase", "ContinuousQuery", "BackFill", "DropDatabase" })
                Assert.DoesNotContain(icon, shown);
            foreach (var icon in new[] { "Users", "Stats", "Diagnostics", "ShowQueries", "TagKeys", "TagValues", "FieldKeys", "Series", "DropSeries", "DropMeasurement" })
                Assert.Contains(icon, shown);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TheToolbarFollowsTheSelection_BetweenDatabases()
    {
        var window = Create();
        try
        {
            var vm = (MainWindowViewModel)window.DataContext!;
            var influx = AddServer(vm, DatabaseKind.InfluxDb1, "influx-1");
            var crate = AddServer(vm, DatabaseKind.CrateDb, "crate-1");

            vm.SelectedNode = crate;
            Assert.False(Buttons(window)["RetentionPolicy"].IsVisible);
            vm.SelectedNode = influx;
            Assert.True(Buttons(window)["RetentionPolicy"].IsVisible);
            Assert.True(Buttons(window)["CreateDatabase"].IsVisible);
            vm.SelectedNode = crate;
            Assert.False(Buttons(window)["CreateDatabase"].IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TheTooltips_UseCrateDbsWords()
    {
        var window = Create();
        try
        {
            var vm = (MainWindowViewModel)window.DataContext!;
            vm.SelectedNode = AddServer(vm, DatabaseKind.CrateDb, "crate-1");
            var buttons = Buttons(window);
            Assert.Equal("Show Text Columns", Tip(buttons["TagKeys"]));
            Assert.Equal("Show Column Values", Tip(buttons["TagValues"]));
            Assert.Equal("Show Columns", Tip(buttons["FieldKeys"]));
            Assert.Equal("Show Partitions", Tip(buttons["Series"]));
            Assert.Equal("Delete All Rows", Tip(buttons["DropSeries"]));
            Assert.Equal("Drop Table", Tip(buttons["DropMeasurement"]));
            Assert.Equal("Show Users", Tip(buttons["Users"]));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void InfluxDb_KeepsItsOwnWords()
    {
        var window = Create();
        try
        {
            var vm = (MainWindowViewModel)window.DataContext!;
            vm.SelectedNode = AddServer(vm, DatabaseKind.InfluxDb1, "influx-1");
            var buttons = Buttons(window);
            Assert.Equal("Create Database", Tip(buttons["CreateDatabase"]));
            Assert.Equal("Drop Database", Tip(buttons["DropDatabase"]));
            Assert.Equal("Show Tag Keys", Tip(buttons["TagKeys"]));
            Assert.Equal("Show Series", Tip(buttons["Series"]));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Separators_AreShownOnlyBetweenGroupsThatHaveButtons()
    {
        var window = Create();
        try
        {
            var vm = (MainWindowViewModel)window.DataContext!;
            var toolbar = window.FindControl<StackPanel>("ToolBar")!;
            vm.SelectedNode = AddServer(vm, DatabaseKind.CrateDb, "crate-1");

            Control? previous = null;
            var buttonSeen = false;
            foreach (var child in toolbar.Children.Where(c => c.IsVisible))
            {
                if (child is Separator)
                {
                    Assert.True(buttonSeen, "a separator must not start the toolbar or follow another separator");
                    Assert.False(previous is Separator);
                    buttonSeen = false;
                }
                else buttonSeen = true;
                previous = child;
            }
            Assert.True(buttonSeen, "the toolbar must not end with a separator");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TheCapabilityMap_OnlyNamesButtonsTheToolbarHas()
    {
        var window = Create();
        try
        {
            var buttons = Buttons(window);
            Assert.All(MainWindow.ToolbarCapabilities.Keys, icon => Assert.True(buttons.ContainsKey(icon), icon + " has no toolbar button"));
        }
        finally { window.Close(); }
    }
}
