using System.Collections.ObjectModel;
using Tickbird.Core.Data;
using Tickbird.Core.Navigation;

namespace Tickbird.Desktop.ViewModels;

/// <summary>
/// Group folder / connection / database / measurement node. Unloaded nodes hold a "loading..." placeholder child.
/// Group folders have no connection; connection nodes have no parent even when shown inside a group folder.
/// </summary>
public sealed class TreeNodeViewModel : ViewModelBase
{
    private string name;
    private bool isExpanded;
    private bool isShown = true;

    public TreeNodeViewModel(string name, NodeKind kind, TreeNodeViewModel? parent = null, DatabaseConnection? connection = null)
    {
        this.name = name; Kind = kind; Parent = parent; this.connection = connection;
    }

    private readonly DatabaseConnection? connection;

    public string Name { get => name; set => Set(ref name, value); }
    public NodeKind Kind { get; }
    public TreeNodeViewModel? Parent { get; }
    public ObservableCollection<TreeNodeViewModel> Children { get; } = new();

    /// <summary>False when the Object Explorer filter hides this node.</summary>
    public bool IsShown { get => isShown; set => Set(ref isShown, value); }

    public bool IsExpanded { get => isExpanded; set { if (Set(ref isExpanded, value)) Expanded?.Invoke(this); } }

    /// <summary>Raised when the node is expanded; the owner lazily loads children.</summary>
    public event Action<TreeNodeViewModel>? Expanded;

    /// <summary>True for nodes that belong to a server (connection, database, measurement).</summary>
    public bool HasConnection => Kind is NodeKind.Connection or NodeKind.Database or NodeKind.Measurement;

    public DatabaseConnection Connection => connection ?? Parent?.Connection ?? throw new InvalidOperationException("Node has no connection.");

    public string IconName => Kind switch
    {
        NodeKind.Connection => "Connection",
        NodeKind.Database => "Database",
        NodeKind.Measurement => "Measurement",
        NodeKind.Group => "Group",
        _ => "Refresh",
    };

    public bool NeedsLoad => Children.Count == 1 && Children[0].Kind == NodeKind.Loading;

    public string? DatabaseName => Kind switch
    {
        NodeKind.Database => Name,
        NodeKind.Measurement => Parent?.Name,
        _ => null,
    };

    public void ResetToPlaceholder()
    {
        Children.Clear();
        Children.Add(new TreeNodeViewModel("loading...", NodeKind.Loading, this));
    }
}
