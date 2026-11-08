using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Tickbird.Core.Data;
using Tickbird.Core.Settings;
using Tickbird.Desktop.Services;
using Tickbird.Desktop.Views.Requests;

namespace Tickbird.Desktop.Views;

/// <summary>Create / edit / remove / connect (port of ManageConnectionsDialog).</summary>
public sealed class ManageConnectionsWindow : Window
{
    private readonly TickbirdApp app;
    private readonly DataGrid grid = GridFactory.Create();
    private readonly Button connect = new() { Content = "Connect", IsEnabled = false, MinWidth = 80 };
    private readonly Button edit = new() { Content = "Edit", IsEnabled = false, MinWidth = 80 };
    private readonly Button remove = new() { Content = "Remove", IsEnabled = false, MinWidth = 80 };
    private readonly ComboBox groupFilter = new() { Width = 250, Height = 40, VerticalAlignment = VerticalAlignment.Center };
    private bool updatingFilter;

    private const string AllGroupsLabel = "All groups";

    public event Action<DatabaseConnection>? ConnectionCreated;
    public event Action<DatabaseConnection>? ConnectionUpdated;
    public event Action<DatabaseConnection>? ConnectionRemoved;

    public ManageConnectionsWindow(TickbirdApp app)
    {
        this.app = app;
        Title = "Manage Connections";
        Width = 1180;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        GridFactory.SetColumns(grid, new[] { "Name", "Group", "Server", "Host", "Active" });
        grid.SelectionMode = DataGridSelectionMode.Single;
        grid.SelectionChanged += (_, _) => UpdateButtons();
        grid.DoubleTapped += (_, _) => { if (connect.IsEnabled) Close(Selected); };

        edit.Click += async (_, _) => await Guard(EditAsync);
        remove.Click += async (_, _) => await Guard(RemoveAsync);
        connect.Click += (_, _) => Close(Selected);
        var cancel = new Button { Content = "Close", MinWidth = 80, IsCancel = true };
        cancel.Click += (_, _) => Close(null);

        Classes.Add("manageConnections");
        grid.Classes.Add("connectionsTable");
        grid.Foreground = Brush.Parse("#1a65a7");
        grid.RowBackground = Brushes.White;
        grid.HorizontalGridLinesBrush = Brush.Parse("#E0E3E8");
        grid.VerticalGridLinesBrush = Brush.Parse("#E0E3E8");
        grid.LoadingRow += (_, e) => e.Row.Background = Brushes.White;
        grid.RowHeight = 36;
        grid.ColumnHeaderHeight = 34;
        grid.BorderThickness = new Thickness(0);
        var nameTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<GridRow>((_, _) =>
        {
            var nameCell = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(10, 0), VerticalAlignment = VerticalAlignment.Center };
            nameCell.Children.Add(new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M4,5 C4,1 20,1 20,5 C20,9 4,9 4,5 M4,5 L4,19 C4,23 20,23 20,19 L20,5 M4,12 C4,16 20,16 20,12"),
                Stroke = Brush.Parse("#1a65a7"), StrokeThickness = 1.5,
                Width = 16, Height = 16, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center
            });
            var nameLabel = new TextBlock { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            nameLabel.Bind(TextBlock.TextProperty, new Binding("[0]"));
            nameCell.Children.Add(nameLabel);
            return nameCell;
        });
        grid.Columns[0] = new DataGridTemplateColumn { Header = "Name", CellTemplate = nameTemplate, SortMemberPath = "[0]", IsReadOnly = true };
        grid.Columns[0].Width = new DataGridLength(1.2, DataGridLengthUnitType.Star);
        grid.Columns[1].Width = new DataGridLength(0.8, DataGridLengthUnitType.Star);
        grid.Columns[2].Width = new DataGridLength(0.9, DataGridLengthUnitType.Star);
        grid.Columns[3].Width = new DataGridLength(1.7, DataGridLengthUnitType.Star);
        grid.Columns[4].Width = new DataGridLength(0.55, DataGridLengthUnitType.Star);
        grid.Resources["SystemControlHighlightListAccentLowBrush"] = Brush.Parse("#dfe9f3");
        grid.Resources["SystemControlHighlightListAccentMediumBrush"] = Brush.Parse("#dfe9f3");
        grid.Resources["SystemControlHighlightListAccentHighBrush"] = Brush.Parse("#dfe9f3");
        StyleAction(edit, "Edit", "M4,17 L17,4 L21,8 L8,21 L3,22 Z M14,7 L18,11");
        StyleAction(remove, "Remove", "M4,6 L20,6 M9,6 L9,3 L15,3 L15,6 M6,6 L7,22 L17,22 L18,6 M10,10 L10,18 M14,10 L14,18");
        StyleAction(connect, "Connect", "M8,3 L8,8 M16,3 L16,8 M5,8 L19,8 L19,11 C19,15 16,18 12,18 C8,18 5,15 5,11 Z M12,18 L12,23");
        StyleAction(cancel, "Close", "M6,6 L18,18 M18,6 L6,18");
        var add = new Button { MinWidth = 200 };
        StyleAction(add, "Add New Connection", "M12,3 L12,21 M3,12 L21,12");
        add.Click += async (_, _) => await Guard(CreateAsync);
        var groups = new Button { MinWidth = 100 };
        StyleAction(groups, "Groups", "M2,6 C2,5 3,4 4,4 L9,4 L11,7 L22,7 C23,7 24,8 24,9 L24,20 C24,21 23,22 22,22 L4,22 C3,22 2,21 2,20 Z M2,11 L24,11");
        ToolTip.SetTip(groups, "Create, rename or delete groups such as PROD or DEV");
        groups.Click += async (_, _) => await Guard(ManageGroupsAsync);
        groupFilter.SelectionChanged += (_, _) => { if (!updatingFilter) Redraw(); };
        add.Classes.Add("connectionPrimary");
        connect.Classes.Add("connectionConnect");
        remove.Classes.Add("connectionDanger");
        ToolTip.SetTip(groupFilter, "Filter connections by group");

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Spacing = 8,
            Children = { groupFilter, add, edit, remove, connect, groups, cancel }
        };
        var layout = new DockPanel();
        var actionScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 0, 0, 12), Content = actions
        };
        DockPanel.SetDock(actionScroll, Dock.Top);
        layout.Children.Add(actionScroll);
        layout.Children.Add(new Border { CornerRadius = new CornerRadius(5), ClipToBounds = true, Child = grid });
        var card = TickbirdTheme.Card(layout);
        card.Padding = new Thickness(18, 14);
        card.ClipToBounds = true;
        Content = card;
        RefreshGroupFilter();
        Redraw();
    }

    /// <summary>The group chosen in the filter, or null for "All groups" / "(No group)".</summary>
    private string? FilterGroup => groupFilter.SelectedItem is string g && g != AllGroupsLabel && g != TickbirdDialogs.NoGroup ? g : null;

    private void RefreshGroupFilter()
    {
        var current = groupFilter.SelectedItem as string;
        var items = new List<string> { AllGroupsLabel };
        items.AddRange(app.Settings.AllGroups());
        items.Add(TickbirdDialogs.NoGroup);
        updatingFilter = true;
        try
        {
            groupFilter.ItemsSource = items;
            groupFilter.SelectedItem = items.FirstOrDefault(i => i == current) ?? AllGroupsLabel;
        }
        finally { updatingFilter = false; }
    }

    private bool MatchesFilter(DatabaseConnection connection) => groupFilter.SelectedItem switch
    {
        TickbirdDialogs.NoGroup => TickbirdSettings.NormalizeGroup(connection.Group) == null,
        string g when g != AllGroupsLabel => TickbirdSettings.SameGroup(connection.Group, g),
        _ => true,
    };

    private async Task ManageGroupsAsync()
    {
        await new ManageGroupsWindow(app.Settings, app.Save).ShowAsync(this);
        RefreshGroupFilter();
        Redraw();
    }

    private static void StyleAction(Button button, string label, string iconPath)
    {
        button.Height = 36;
        button.VerticalAlignment = VerticalAlignment.Center;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(iconPath), StrokeThickness = 1.7,
            Width = 16, Height = 16, Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center
        };
        icon.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty, new Binding("Foreground") { Source = button });
        content.Children.Add(icon);
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        button.Content = content;
        button.Classes.Add("connectionAction");
    }

    private DatabaseConnection? Selected => (grid.SelectedItem as GridRow)?.Tag as DatabaseConnection;

    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { await Dialogs.ShowExceptionAsync(this, ex); }
    }

    public void Redraw()
    {
        var previous = Selected;
        // Grouped connections first (by group name), then ungrouped; saved order is kept within a group.
        var rows = app.Settings.Connections.Where(MatchesFilter)
            .OrderBy(c => TickbirdSettings.NormalizeGroup(c.Group) == null ? 1 : 0)
            .ThenBy(c => TickbirdSettings.NormalizeGroup(c.Group) ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(c => new GridRow(new[]
            {
                c.Name, TickbirdSettings.NormalizeGroup(c.Group) ?? "",
                c.Profile.DisplayName,
                c.Host + ":" + c.Port, app.IsConnected(c) ? GridFactory.CheckMark : null
            }, c)).ToList();
        grid.ItemsSource = rows;
        grid.SelectedItem = rows.FirstOrDefault(r => r.Tag == previous) ?? rows.FirstOrDefault();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var selected = Selected;
        edit.IsEnabled = remove.IsEnabled = selected != null;
        connect.IsEnabled = selected != null && !app.IsConnected(selected);
    }

    private async Task CreateAsync()
    {
        var connection = await TickbirdDialogs.ConnectionAsync(this, null, app.Settings, FilterGroup);
        if (connection == null) return;
        connection.Group = app.Settings.EnsureGroup(connection.Group);
        app.Settings.Connections.Add(connection);
        app.Save();
        ConnectionCreated?.Invoke(connection);
        RefreshGroupFilter();
        Redraw();
        grid.SelectedItem = (grid.ItemsSource as IEnumerable<GridRow>)?.FirstOrDefault(r => r.Tag == connection) ?? grid.SelectedItem;
    }

    private async Task EditAsync()
    {
        var existing = Selected;
        if (existing == null) return;
        var updated = await TickbirdDialogs.ConnectionAsync(this, existing, app.Settings);
        if (updated == null) return;
        existing.Name = updated.Name; existing.Host = updated.Host; existing.Port = updated.Port;
        existing.Database = updated.Database; existing.Username = updated.Username; existing.Password = updated.Password;
        existing.UseSsl = updated.UseSsl; existing.ServerVersion = updated.ServerVersion; existing.Kind = updated.Kind; existing.Token = updated.Token;
        existing.Group = app.Settings.EnsureGroup(updated.Group);
        app.Save();
        ConnectionUpdated?.Invoke(existing);
        RefreshGroupFilter();
        Redraw();
    }

    private async Task RemoveAsync()
    {
        var connection = Selected;
        if (connection == null) return;
        if (!await Dialogs.ConfirmAsync(this, "Confirm Delete", $"Are you sure you want to delete the connection \"{connection.Name}\"?")) return;
        app.Settings.Connections.RemoveAll(c => c.Id == connection.Id);
        app.Save();
        ConnectionRemoved?.Invoke(connection);
        Redraw();
    }

    /// <summary>Shows the dialog; resolves to the connection the user chose to connect to, if any.</summary>
    public async Task<DatabaseConnection?> ShowAsync(Window owner) => await ShowDialog<DatabaseConnection?>(owner);
}
