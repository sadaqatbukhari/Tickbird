using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Tickbird.Core.Data;
using Tickbird.Core.Providers;
using Tickbird.Desktop.Services;
using Tickbird.Desktop.Views;
using Tickbird.Core.Settings;

namespace Tickbird.Desktop.Tests;

/// <summary>The Manage Connections list: grouped by database type, then by group, in a small window.</summary>
public class ManageConnectionsTests
{
    private static DatabaseConnection Make(string name, DatabaseKind kind, string? group = null) =>
        new(Guid.NewGuid().ToString(), name, name.ToLowerInvariant() + ".example.com", 5432, "u", "p", false) { Kind = kind, Group = group };

    private static string Shape(IEnumerable<ConnectionListEntry> entries) => string.Join(" | ", entries.Select(e => e.Kind switch
    {
        ConnectionListEntryKind.TypeHeading => $"T:{e.Title}({e.Count})",
        ConnectionListEntryKind.GroupHeading => $"G:{e.Title}({e.Count})",
        _ => e.Title
    }));

    [Fact]
    public void SeveralDatabaseTypes_GroupByTypeFirst_ThenByGroup()
    {
        var entries = ConnectionListLayout.Build(new[]
        {
            Make("Crate prod", DatabaseKind.CrateDb, "PROD"),
            Make("Influx dev", DatabaseKind.InfluxDb1, "DEV"),
            Make("Influx loose", DatabaseKind.InfluxDb1),
            Make("Influx prod", DatabaseKind.InfluxDb1, "PROD"),
            Make("Timescale", DatabaseKind.TimescaleDb),
        });
        string Name(DatabaseKind k) => DatabaseProfiles.Get(k).DisplayName;
        Assert.Equal(
            $"T:{Name(DatabaseKind.InfluxDb1)}(3) | G:DEV(1) | Influx dev | G:PROD(1) | Influx prod | G:No group(1) | Influx loose | " +
            $"T:{Name(DatabaseKind.TimescaleDb)}(1) | Timescale | " +
            $"T:{Name(DatabaseKind.CrateDb)}(1) | G:PROD(1) | Crate prod",
            Shape(entries));
    }

    [Fact]
    public void OneDatabaseType_HasNoTypeHeadings()
    {
        var entries = ConnectionListLayout.Build(new[]
        {
            Make("A", DatabaseKind.TimescaleDb, "PROD"), Make("B", DatabaseKind.TimescaleDb)
        });
        Assert.DoesNotContain(entries, e => e.Kind == ConnectionListEntryKind.TypeHeading);
        Assert.Equal("G:PROD(1) | A | G:No group(1) | B", Shape(entries));
    }

    [Fact]
    public void NoGroups_GiveAFlatListUnderEachType()
    {
        var entries = ConnectionListLayout.Build(new[] { Make("A", DatabaseKind.CrateDb), Make("B", DatabaseKind.TimescaleDb) });
        Assert.DoesNotContain(entries, e => e.Kind == ConnectionListEntryKind.GroupHeading);
        Assert.Equal($"T:{DatabaseProfiles.Get(DatabaseKind.TimescaleDb).DisplayName}(1) | B | T:{DatabaseProfiles.Get(DatabaseKind.CrateDb).DisplayName}(1) | A", Shape(entries));
    }

    [Fact]
    public void Search_FiltersWithoutChangingTheShape()
    {
        var all = new[] { Make("Alpha", DatabaseKind.CrateDb), Make("Beta", DatabaseKind.TimescaleDb) };
        // Still two types in use, so the type heading stays even when only one connection matches.
        Assert.Equal($"T:{DatabaseProfiles.Get(DatabaseKind.CrateDb).DisplayName}(1) | Alpha", Shape(ConnectionListLayout.Build(all, "alpha")));
        Assert.Empty(ConnectionListLayout.Build(all, "nothing like this"));
        Assert.Single(ConnectionListLayout.Build(all, "BETA.example"), e => e.Kind == ConnectionListEntryKind.Connection);
    }

    [Fact]
    public void Search_MatchesGroupAndDatabaseType()
    {
        var all = new[] { Make("One", DatabaseKind.CrateDb, "PROD"), Make("Two", DatabaseKind.CrateDb, "DEV") };
        Assert.Equal(new[] { "One" }, ConnectionListLayout.Build(all, "prod").Where(e => e.Connection != null).Select(e => e.Title));
        Assert.Equal(2, ConnectionListLayout.Build(all, "cratedb").Count(e => e.Connection != null));
    }

    private static TickbirdApp NewApp(params DatabaseConnection[] connections)
    {
        var app = new TickbirdApp(new SettingsStore(Path.Combine(Path.GetTempPath(), "tickbird-manage-" + Guid.NewGuid().ToString("N"), "settings.json")));
        TickbirdApp.Current = app;
        app.Settings.Connections.AddRange(connections);
        return app;
    }

    private static void Pump() { for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs(); }

    private static List<Border> Rows(Window w) =>
        w.GetLogicalDescendants().OfType<Border>().Where(b => b.Classes.Contains("connectionRow")).ToList();

    [AvaloniaFact]
    public void TheWindow_IsSmall_AndShowsHeadingsAndRows()
    {
        var app = NewApp(Make("Crate", DatabaseKind.CrateDb, "PROD"), Make("Influx", DatabaseKind.InfluxDb1));
        var window = new ManageConnectionsWindow(app);
        window.Show();
        window.UpdateLayout();
        Assert.True(window.Width <= 760 && window.Height <= 520, $"{window.Width}x{window.Height}");
        Assert.Equal(2, window.GetLogicalDescendants().OfType<Border>().Count(b => b.Classes.Contains("connectionTypeHeading")));
        Assert.Equal(1, window.GetLogicalDescendants().OfType<Border>().Count(b => b.Classes.Contains("connectionGroupHeading")));
        Assert.Equal(2, Rows(window).Count);
        window.Close();
    }

    [AvaloniaFact]
    public void TheFirstConnection_IsSelected_AndTheButtonsFollowTheSelection()
    {
        var app = NewApp(Make("Only", DatabaseKind.CrateDb));
        var window = new ManageConnectionsWindow(app);
        window.Show();
        Assert.True(window.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "EditButton").IsEnabled);
        Assert.True(window.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "ConnectButton").IsEnabled);

        app.Settings.Connections.Clear();
        window.Redraw();
        Assert.False(window.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "EditButton").IsEnabled);
        Assert.Contains("No connections yet", window.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Name == "ConnectionListEmpty").Text);
        window.Close();
    }

    [AvaloniaFact]
    public void CollapsingATypeOrGroup_HidesItsRows_AndSearchShowsThemAgain()
    {
        var app = NewApp(Make("C1", DatabaseKind.CrateDb, "PROD"), Make("C2", DatabaseKind.CrateDb, "DEV"), Make("T1", DatabaseKind.TimescaleDb));
        var window = new ManageConnectionsWindow(app);
        window.Show();
        Assert.Equal(3, Rows(window).Count);

        window.Toggle(ManageConnectionsWindow.GroupKey(DatabaseKind.CrateDb, "PROD"));
        Assert.Equal(new[] { "C2", "T1" }.Order(), Rows(window).Select(r => ((DatabaseConnection)r.Tag!).Name).Order());

        window.Toggle(ManageConnectionsWindow.TypeKey(DatabaseKind.CrateDb));
        Assert.Equal(new[] { "T1" }, Rows(window).Select(r => ((DatabaseConnection)r.Tag!).Name));
        Assert.Equal(2, window.GetLogicalDescendants().OfType<Border>().Count(b => b.Classes.Contains("connectionTypeHeading")));
        Assert.Equal(0, window.GetLogicalDescendants().OfType<Border>().Count(b => b.Classes.Contains("connectionGroupHeading")));

        var search = window.GetLogicalDescendants().OfType<TextBox>().Single(t => t.Name == "ConnectionSearch");
        search.Text = "c1";
        Pump();
        Assert.Single(Rows(window));   // collapsed sections open while searching

        search.Text = "";
        Pump();
        window.Toggle(ManageConnectionsWindow.TypeKey(DatabaseKind.CrateDb));
        // Expanding the type again brings back its rows, except the group that is still collapsed.
        Assert.Equal(new[] { "C2", "T1" }.Order(), Rows(window).Select(r => ((DatabaseConnection)r.Tag!).Name).Order());
        window.Close();
    }

    [AvaloniaFact]
    public void TheOpenMarker_IsShownOnlyForAConnectedRow_NextToTheAddress()
    {
        var app = NewApp(Make("Idle", DatabaseKind.CrateDb));
        var window = new ManageConnectionsWindow(app);
        window.Show();
        var marker = window.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Name == "OpenMarker");
        Assert.False(marker.IsVisible);
        // Name, address and marker are columns of one grid: the address and the marker are right-aligned, the address
        // column shares its width across rows and the marker column has a fixed width.
        var grid = Assert.IsType<Grid>(marker.GetLogicalParent());
        Assert.Equal(3, grid.Children.OfType<TextBlock>().Count());
        Assert.Equal("ConnectionAddress", grid.ColumnDefinitions[2].SharedSizeGroup);
        Assert.Equal(64, grid.ColumnDefinitions[3].Width.Value);
        Assert.All(grid.Children.OfType<TextBlock>().Where(t => Grid.GetColumn(t) >= 2), t => Assert.Equal(Avalonia.Layout.HorizontalAlignment.Right, t.HorizontalAlignment));
        Assert.Equal(new[] { 1, 2, 3 }, grid.Children.OfType<TextBlock>().Select(Grid.GetColumn));
        window.Close();
    }

    [AvaloniaFact]
    public void AConnection_ShowsItsOwnColour_OtherwiseItsGroupsColour()
    {
        var own = Make("Own", DatabaseKind.CrateDb, "PROD"); own.Color = "#D64545";
        var inherits = Make("Inherits", DatabaseKind.CrateDb, "PROD");
        var plain = Make("Plain", DatabaseKind.CrateDb);
        var app = NewApp(own, inherits, plain);
        app.Settings.SetGroupColor("PROD", "#2F6FD0");
        var window = new ManageConnectionsWindow(app);
        window.Show();
        var colours = window.GetLogicalDescendants().OfType<Border>().Where(b => b.Name == "ConnectionColorDot").Select(b => (string?)b.Tag).ToList();
        Assert.Equal(new string?[] { "#D64545", "#2F6FD0", null }, colours);
        Assert.Equal("#2F6FD0", Assert.Single(window.GetLogicalDescendants().OfType<Border>(), b => b.Name == "GroupColorDot").Tag);
        window.Close();
    }

    [Fact]
    public void GroupColours_FollowARenameAndGoWithADelete()
    {
        var settings = new TickbirdSettings();
        settings.EnsureGroup("PROD");
        settings.SetGroupColor("prod", "#d64545");
        Assert.Equal("#D64545", settings.GroupColor("PROD"));
        settings.RenameGroup("PROD", "LIVE");
        Assert.Null(settings.GroupColor("PROD"));
        Assert.Equal("#D64545", settings.GroupColor("live"));
        settings.RemoveGroup("LIVE");
        Assert.Empty(settings.GroupColors);
        settings.SetGroupColor("X", "not a colour");
        Assert.Empty(settings.GroupColors);
    }

    [Fact]
    public void AConnectionColour_BeatsItsGroupColour_AndBadValuesAreIgnored()
    {
        var settings = new TickbirdSettings();
        settings.SetGroupColor("PROD", "#2F6FD0");
        var c = Make("A", DatabaseKind.CrateDb, "PROD");
        Assert.Equal("#2F6FD0", settings.ColorOf(c));
        c.Color = "#d64545";
        Assert.Equal("#D64545", settings.ColorOf(c));
        c.Color = "red";
        Assert.Equal("#2F6FD0", settings.ColorOf(c));
    }

    [AvaloniaFact]
    public async Task TheConnectionDialog_SavesTheChosenColour()
    {
        var owner = new Window();
        owner.Show();
        var existing = Make("Ex", DatabaseKind.CrateDb);
        var result = TickbirdDialogs.ConnectionAsync(owner, existing, new TickbirdSettings());
        if (result.IsFaulted) await result;
        var dialog = Assert.Single(owner.OwnedWindows);
        var swatches = dialog.GetLogicalDescendants().OfType<ColorSwatches>().Single();
        Assert.Null(swatches.Value);
        swatches.Pick("#7A55C4");
        var save = dialog.GetLogicalDescendants().OfType<Button>().First(b => b.IsDefault);
        save.Command?.Execute(null);
        save.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump();
        var saved = await result;
        Assert.Equal("#7A55C4", saved?.Color);
        owner.Close();
    }

    [AvaloniaFact]
    public void TypingInTheSearchBox_FiltersTheRows()
    {
        var app = NewApp(Make("Alpha", DatabaseKind.CrateDb), Make("Beta", DatabaseKind.TimescaleDb));
        var window = new ManageConnectionsWindow(app);
        window.Show();
        var search = window.GetLogicalDescendants().OfType<TextBox>().Single(t => t.Name == "ConnectionSearch");
        search.Text = "beta";
        Pump();
        Assert.Single(Rows(window));
        search.Text = "zzz";
        Pump();
        Assert.Empty(Rows(window));
        Assert.Contains("No connection matches", window.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Name == "ConnectionListEmpty").Text);
        window.Close();
    }
}
