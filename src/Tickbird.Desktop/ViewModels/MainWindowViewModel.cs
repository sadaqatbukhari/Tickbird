using System.Collections.ObjectModel;
using Tickbird.Core.Data;
using Tickbird.Core.Providers;
using Tickbird.Core.Navigation;
using Tickbird.Core.Settings;

namespace Tickbird.Desktop.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private TreeNodeViewModel? selectedNode;
    private TabViewModel? selectedTab;
    private string status = "";
    private CommandAvailability commands = CommandAvailability.For(NodeKind.None, null, (DatabaseProfile?)null, false, false);

    public ObservableCollection<TreeNodeViewModel> Nodes { get; } = new();
    public ObservableCollection<TabViewModel> Tabs { get; } = new();

    public TreeNodeViewModel? SelectedNode
    {
        get => selectedNode;
        set { if (Set(ref selectedNode, value)) UpdateCommands(); }
    }

    public TabViewModel? SelectedTab
    {
        get => selectedTab;
        set { if (Set(ref selectedTab, value)) UpdateCommands(); }
    }

    public string Status { get => status; set => Set(ref status, value); }

    private string treeFilter = "";

    /// <summary>The Object Explorer filter text ("" shows everything).</summary>
    public string TreeFilter => treeFilter;

    /// <summary>
    /// Shows only nodes whose name contains <paramref name="text"/> (ignoring case), the folders above them, and
    /// everything below a match (a matching database shows all its measurements). Folders above a match are expanded.
    /// </summary>
    public void ApplyTreeFilter(string? text)
    {
        treeFilter = text?.Trim() ?? "";
        foreach (var node in Nodes) ApplyFilter(node, ancestorMatches: false);
    }

    /// <summary>Re-applies the current filter, for nodes loaded after it was typed.</summary>
    public void ReapplyTreeFilter() => ApplyTreeFilter(treeFilter);

    /// <summary>Count of nodes whose own name matches the filter (for the status line and tests).</summary>
    public int CountTreeMatches() => treeFilter.Length == 0 ? 0 : Nodes.Sum(CountMatches);

    private int CountMatches(TreeNodeViewModel node) =>
        (node.Kind is not (NodeKind.Group or NodeKind.Loading) && node.Name.Contains(treeFilter, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
        + node.Children.Sum(CountMatches);

    private bool ApplyFilter(TreeNodeViewModel node, bool ancestorMatches)
    {
        if (treeFilter.Length == 0)
        {
            node.IsShown = true;
            foreach (var child in node.Children) ApplyFilter(child, false);
            return true;
        }
        if (node.Kind == NodeKind.Loading)
        {
            node.IsShown = ancestorMatches;
            return false;
        }
        var self = node.Kind != NodeKind.Group && node.Name.Contains(treeFilter, StringComparison.OrdinalIgnoreCase);
        var below = false;
        foreach (var child in node.Children) below |= ApplyFilter(child, ancestorMatches || self);
        node.IsShown = self || below || ancestorMatches;
        if (below && !node.IsExpanded) node.IsExpanded = true;   // reveal the matches
        return self || below;
    }

    public CommandAvailability Commands { get => commands; private set => Set(ref commands, value); }

    /// <summary>Recomputes which toolbar/menu commands are enabled (port of UpdateUIState).</summary>
    public void UpdateCommands()
    {
        var node = SelectedNode;
        DatabaseProfile? profile = null;
        if (node != null && node.HasConnection) profile = node.Connection.Profile;
        Commands = CommandAvailability.For(node?.Kind ?? NodeKind.None, node?.Name, profile,
            SelectedTab?.IsRequest == true, SelectedTab?.IsQuery == true);
    }

    public TabViewModel? FindTab(string id) => Tabs.FirstOrDefault(t => t.Id == id);

    public TabViewModel AddTab(TabViewModel tab)
    {
        Tabs.Add(tab);
        SelectedTab = tab;
        return tab;
    }

    /// <summary>Id for a New Query tab: always unique, so every invocation opens an independent tab.</summary>
    public static string NewQueryTabId() => TabIdentity.NewQuery();

    /// <summary>Id for the Show Queries tab: one per connection, keyed by connection ID (not display name).</summary>
    public static string ShowQueriesTabId(DatabaseConnection connection) => TabIdentity.ShowQueries(connection.Id);

    /// <summary>Selects the existing tab with this ID and returns true; otherwise returns false.</summary>
    public bool FocusExisting(string id)
    {
        var tab = FindTab(id);
        if (tab == null) return false;
        SelectedTab = tab;
        return true;
    }

    public void CloseTab(TabViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0) return;
        Tabs.RemoveAt(index);
        if (SelectedTab == tab) SelectedTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
    }

    public void CloseAll()
    {
        Tabs.Clear();
        SelectedTab = null;
    }

    public void CloseAllBut(TabViewModel keep)
    {
        foreach (var tab in Tabs.Where(t => t != keep).ToList()) Tabs.Remove(tab);
        SelectedTab = keep;
    }

    public void CloseTabsForConnection(string connectionId)
    {
        foreach (var tab in Tabs.Where(t => t.ConnectionId == connectionId).ToList()) CloseTab(tab);
    }

    public TreeNodeViewModel? FindConnectionNode(string connectionId) =>
        ConnectionNodes().FirstOrDefault(n => n.Connection.Id == connectionId);

    // ------------------------------------------------------------------ connection groups

    /// <summary>Connected server nodes, whether at the top level or inside a group folder.</summary>
    public IEnumerable<TreeNodeViewModel> ConnectionNodes() =>
        Nodes.SelectMany(n => n.Kind == NodeKind.Group ? n.Children.AsEnumerable() : new[] { n })
             .Where(n => n.Kind == NodeKind.Connection);

    public TreeNodeViewModel? FindGroupNode(string group) =>
        Nodes.FirstOrDefault(n => n.Kind == NodeKind.Group && TickbirdSettings.SameGroup(n.Name, group));

    /// <summary>
    /// Adds a connected server to the tree under its group folder, creating the folder if needed.
    /// Group folders are kept sorted at the top; ungrouped servers follow at the top level.
    /// </summary>
    public void AddConnectionNode(TreeNodeViewModel node)
    {
        var group = TickbirdSettings.NormalizeGroup(node.Connection.Group);
        if (group == null)
        {
            Nodes.Add(node);
            return;
        }
        var folder = FindGroupNode(group);
        if (folder == null)
        {
            folder = new TreeNodeViewModel(group, NodeKind.Group);
            var groupCount = Nodes.Count(n => n.Kind == NodeKind.Group);
            var index = Nodes.Take(groupCount).Count(n => string.Compare(n.Name, group, StringComparison.OrdinalIgnoreCase) < 0);
            Nodes.Insert(index, folder);
        }
        folder.Children.Add(node);
        folder.IsExpanded = true;
    }

    /// <summary>Removes a connected server from the tree; a group folder left empty is removed too.</summary>
    public void RemoveConnectionNode(TreeNodeViewModel node)
    {
        if (Nodes.Remove(node)) return;
        var folder = FolderOf(node);
        if (folder == null) return;
        folder.Children.Remove(node);
        if (folder.Children.Count == 0) Nodes.Remove(folder);
    }

    /// <summary>
    /// Moves connected servers to match their current group (after a connection is edited or a group is
    /// renamed or deleted). Does nothing when every server is already in the right place.
    /// </summary>
    public void RegroupConnections()
    {
        var connections = ConnectionNodes().ToList();
        var inPlace = connections.All(n => string.Equals(FolderOf(n)?.Name, TickbirdSettings.NormalizeGroup(n.Connection.Group), StringComparison.Ordinal));
        if (inPlace) return;

        var selected = SelectedNode;
        var collapsed = Nodes.Where(n => n.Kind == NodeKind.Group && !n.IsExpanded).Select(n => n.Name).ToList();
        Nodes.Clear();
        foreach (var node in connections) AddConnectionNode(node);
        foreach (var folder in Nodes.Where(n => n.Kind == NodeKind.Group && collapsed.Any(c => TickbirdSettings.SameGroup(c, n.Name))))
            folder.IsExpanded = false;
        SelectedNode = selected != null && IsInTree(selected) ? selected : Nodes.FirstOrDefault();
    }

    /// <summary>True when the node (or the server it belongs to) is currently shown in the tree.</summary>
    public bool IsInTree(TreeNodeViewModel node)
    {
        var root = node;
        while (root.Parent != null) root = root.Parent;
        return root.Kind == NodeKind.Group ? Nodes.Contains(root) : ConnectionNodes().Contains(root);
    }

    private TreeNodeViewModel? FolderOf(TreeNodeViewModel node) =>
        Nodes.FirstOrDefault(n => n.Kind == NodeKind.Group && n.Children.Contains(node));
}
