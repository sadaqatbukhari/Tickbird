using Tickbird.Core.Data;
using Tickbird.Core.Navigation;
using Tickbird.Desktop.ViewModels;

namespace Tickbird.Desktop.Tests;

public class GroupTreeTests
{
    private static TreeNodeViewModel Server(string id, string? group) =>
        new(id, NodeKind.Connection, null, new DatabaseConnection(id, id, "localhost", 8086, "u", "p") { Group = group });

    private static string[] Top(MainWindowViewModel vm) => vm.Nodes.Select(n => n.Name).ToArray();

    [Fact]
    public void AddConnectionNode_PutsServersUnderSortedGroupFolders_UngroupedLast()
    {
        var vm = new MainWindowViewModel();
        vm.AddConnectionNode(Server("solo", null));
        vm.AddConnectionNode(Server("p1", "PROD"));
        vm.AddConnectionNode(Server("d1", "DEV"));
        vm.AddConnectionNode(Server("p2", "prod"));

        Assert.Equal(new[] { "DEV", "PROD", "solo" }, Top(vm));
        var prod = vm.FindGroupNode("PROD")!;
        Assert.Equal(NodeKind.Group, prod.Kind);
        Assert.True(prod.IsExpanded);
        Assert.Equal(new[] { "p1", "p2" }, prod.Children.Select(c => c.Name));
        Assert.Equal("Group", prod.IconName);
    }

    [Fact]
    public void FindConnectionNode_LooksInsideGroups()
    {
        var vm = new MainWindowViewModel();
        var p1 = Server("p1", "PROD");
        vm.AddConnectionNode(p1);
        Assert.Same(p1, vm.FindConnectionNode("p1"));
        Assert.Single(vm.ConnectionNodes());
    }

    [Fact]
    public void RemoveConnectionNode_RemovesEmptiedFolderOnly()
    {
        var vm = new MainWindowViewModel();
        var p1 = Server("p1", "PROD");
        var p2 = Server("p2", "PROD");
        vm.AddConnectionNode(p1);
        vm.AddConnectionNode(p2);
        vm.RemoveConnectionNode(p1);
        Assert.Equal(new[] { "PROD" }, Top(vm));
        vm.RemoveConnectionNode(p2);
        Assert.Empty(vm.Nodes);
    }

    [Fact]
    public void RegroupConnections_MovesServerAfterItsGroupChanges_AndKeepsSelection()
    {
        var vm = new MainWindowViewModel();
        var a = Server("a", "DEV");
        vm.AddConnectionNode(a);
        vm.AddConnectionNode(Server("b", "DEV"));
        vm.SelectedNode = a;

        a.Connection.Group = "PROD";
        vm.RegroupConnections();

        Assert.Equal(new[] { "DEV", "PROD" }, Top(vm));
        Assert.Same(a, vm.FindGroupNode("PROD")!.Children.Single());
        Assert.Same(a, vm.SelectedNode);
    }

    [Fact]
    public void RegroupConnections_RenamedGroup_UpdatesFolderName_AndKeepsCollapsedState()
    {
        var vm = new MainWindowViewModel();
        var a = Server("a", "dev");
        vm.AddConnectionNode(a);
        vm.FindGroupNode("dev")!.IsExpanded = false;

        a.Connection.Group = "DEV";
        vm.RegroupConnections();

        var folder = Assert.Single(vm.Nodes);
        Assert.Equal("DEV", folder.Name);
        Assert.False(folder.IsExpanded);
    }

    [Fact]
    public void RegroupConnections_DeletedGroup_MovesServerToTopLevel()
    {
        var vm = new MainWindowViewModel();
        var a = Server("a", "QA");
        vm.AddConnectionNode(a);
        a.Connection.Group = null;
        vm.RegroupConnections();
        Assert.Same(a, Assert.Single(vm.Nodes));
    }

    [Fact]
    public void RegroupConnections_DoesNothingWhenAlreadyInPlace()
    {
        var vm = new MainWindowViewModel();
        vm.AddConnectionNode(Server("a", "PROD"));
        var folder = vm.Nodes[0];
        vm.RegroupConnections();
        Assert.Same(folder, vm.Nodes[0]);
    }

    [Fact]
    public void SelectingGroupFolder_DisablesServerCommands()
    {
        var vm = new MainWindowViewModel();
        vm.AddConnectionNode(Server("a", "PROD"));
        vm.SelectedNode = vm.FindGroupNode("PROD");
        Assert.False(vm.Commands.Disconnect);
        Assert.False(vm.Commands.Refresh);
        Assert.False(vm.Commands.CreateDatabase);
    }

    [Fact]
    public void IsInTree_TracksServersAndTheirChildren()
    {
        var vm = new MainWindowViewModel();
        var a = Server("a", "PROD");
        var db = new TreeNodeViewModel("db", NodeKind.Database, a);
        a.Children.Add(db);
        vm.AddConnectionNode(a);
        Assert.True(vm.IsInTree(db));
        vm.RemoveConnectionNode(a);
        Assert.False(vm.IsInTree(db));
    }
}
