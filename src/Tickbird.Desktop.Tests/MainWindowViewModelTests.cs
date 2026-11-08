using Tickbird.Core.Data;
using Tickbird.Core.Navigation;
using Tickbird.Desktop.ViewModels;

namespace Tickbird.Desktop.Tests;

public class MainWindowViewModelTests
{
    private static DatabaseConnection Conn(string id, string name, InfluxDbServerVersion v = InfluxDbServerVersion.InfluxDb1) =>
        new(id, name, "localhost", 8086, "u", "p", false, null, v);

    private static TabViewModel RequestTab(string id, string title, string? connectionId = "1", TabKind kind = TabKind.Request) =>
        new(id, title, kind, connectionId, null, new object());

    [Fact]
    public void NewQueryIds_AreAlwaysUnique() =>
        Assert.NotEqual(MainWindowViewModel.NewQueryTabId(), MainWindowViewModel.NewQueryTabId());

    [Fact]
    public void ShowQueriesId_UsesConnectionId_NotDisplayName()
    {
        Assert.Equal(MainWindowViewModel.ShowQueriesTabId(Conn("1", "Same")), MainWindowViewModel.ShowQueriesTabId(Conn("1", "Renamed")));
        Assert.NotEqual(MainWindowViewModel.ShowQueriesTabId(Conn("1", "Same")), MainWindowViewModel.ShowQueriesTabId(Conn("2", "Same")));
    }

    [Fact]
    public void FocusExisting_SelectsMatchingTab_AndReportsMiss()
    {
        var vm = new MainWindowViewModel();
        var a = vm.AddTab(RequestTab(MainWindowViewModel.ShowQueriesTabId(Conn("1", "A")), "A.queries", kind: TabKind.ShowQueries));
        vm.AddTab(RequestTab("other", "other"));
        Assert.True(vm.FocusExisting(a.Id));
        Assert.Same(a, vm.SelectedTab);
        Assert.False(vm.FocusExisting("missing"));
        Assert.Equal(2, vm.Tabs.Count);
    }

    [Fact]
    public void AddTab_SelectsNewTab()
    {
        var vm = new MainWindowViewModel();
        var t = vm.AddTab(RequestTab("1", "t"));
        Assert.Same(t, vm.SelectedTab);
    }

    [Fact]
    public void CloseTab_SelectsNeighbour_AndCloseLastClearsSelection()
    {
        var vm = new MainWindowViewModel();
        var a = vm.AddTab(RequestTab("a", "a"));
        var b = vm.AddTab(RequestTab("b", "b"));
        vm.CloseTab(b);
        Assert.Same(a, vm.SelectedTab);
        vm.CloseTab(a);
        Assert.Null(vm.SelectedTab);
    }

    [Fact]
    public void CloseAllButThis_KeepsOnlyThatTab()
    {
        var vm = new MainWindowViewModel();
        vm.AddTab(RequestTab("a", "a"));
        var b = vm.AddTab(RequestTab("b", "b"));
        vm.AddTab(RequestTab("c", "c"));
        vm.CloseAllBut(b);
        Assert.Same(b, Assert.Single(vm.Tabs));
        Assert.Same(b, vm.SelectedTab);
    }

    [Fact]
    public void CloseAll_RemovesEverything()
    {
        var vm = new MainWindowViewModel();
        vm.AddTab(RequestTab("a", "a"));
        vm.CloseAll();
        Assert.Empty(vm.Tabs);
        Assert.Null(vm.SelectedTab);
    }

    [Fact]
    public void CloseTabsForConnection_OnlyClosesThatConnectionsTabs()
    {
        var vm = new MainWindowViewModel();
        vm.AddTab(RequestTab("a", "a", "1"));
        vm.AddTab(RequestTab("b", "b", "2"));
        vm.AddTab(RequestTab("c", "c", "1"));
        vm.CloseTabsForConnection("1");
        Assert.Equal("b", Assert.Single(vm.Tabs).Id);
    }

    [Fact]
    public void TabTitle_IsPreservedInFull_ForTooltip()
    {
        var longTitle = new string('x', 500);
        Assert.Equal(longTitle, RequestTab("a", longTitle).Title);
        Assert.Equal(300, TabViewModel.MaxTabWidth);
    }

    [Fact]
    public void Commands_FollowSelectedNode()
    {
        var vm = new MainWindowViewModel();
        Assert.False(vm.Commands.Disconnect);
        var conn = new TreeNodeViewModel("Prod", NodeKind.Connection, null, Conn("1", "Prod"));
        vm.Nodes.Add(conn);
        vm.SelectedNode = conn;
        Assert.True(vm.Commands.CreateDatabase && vm.Commands.ShowUsers && vm.Commands.Disconnect);
        Assert.False(vm.Commands.NewQuery);
    }

    [Fact]
    public void Commands_InfluxDb3Connection_HidesLegacyFeatures()
    {
        var vm = new MainWindowViewModel();
        var conn = new TreeNodeViewModel("V3", NodeKind.Connection, null, Conn("3", "V3", InfluxDbServerVersion.InfluxDb3));
        vm.Nodes.Add(conn);
        vm.SelectedNode = conn;
        Assert.False(vm.Commands.ShowQueries || vm.Commands.ShowUsers || vm.Commands.ShowPolicies);
        Assert.True(vm.Commands.CreateDatabase);
    }

    [Fact]
    public void Commands_RunAndComment_FollowSelectedTab()
    {
        var vm = new MainWindowViewModel();
        vm.AddTab(new TabViewModel("q", "q", TabKind.Query, "1", null, new object()));
        Assert.True(vm.Commands.RunQuery && vm.Commands.CommentQuery && vm.Commands.OpenSaveQuery);
        vm.CloseAll();
        Assert.False(vm.Commands.RunQuery);
    }

    [Fact]
    public void Tree_PlaceholderAndNeedsLoad()
    {
        var conn = new TreeNodeViewModel("c", NodeKind.Connection, null, Conn("1", "c"));
        Assert.False(conn.NeedsLoad);
        conn.ResetToPlaceholder();
        Assert.True(conn.NeedsLoad);
        var db = new TreeNodeViewModel("db", NodeKind.Database, conn);
        var m = new TreeNodeViewModel("cpu", NodeKind.Measurement, db);
        Assert.Equal("db", m.DatabaseName);
        Assert.Equal("1", m.Connection.Id);
    }

    [Fact]
    public void Tree_ExpandRaisesEvent_Once()
    {
        var node = new TreeNodeViewModel("c", NodeKind.Connection, null, Conn("1", "c"));
        var raised = 0;
        node.Expanded += _ => raised++;
        node.IsExpanded = true;
        node.IsExpanded = true;
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Tree_IconNames_MatchLegacyAssets()
    {
        Assert.Equal("Connection", new TreeNodeViewModel("a", NodeKind.Connection, null, Conn("1", "a")).IconName);
        Assert.Equal("Database", new TreeNodeViewModel("a", NodeKind.Database).IconName);
        Assert.Equal("Measurement", new TreeNodeViewModel("a", NodeKind.Measurement).IconName);
    }

    [Fact]
    public void PropertyChanged_RaisedForStatus()
    {
        var vm = new MainWindowViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.Status = "Busy";
        Assert.Contains("Status", raised);
    }
}
