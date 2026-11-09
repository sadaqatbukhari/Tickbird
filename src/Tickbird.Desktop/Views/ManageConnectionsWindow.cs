using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Tickbird.Core.Data;
using Tickbird.Core.Providers;
using Tickbird.Core.Settings;
using Tickbird.Desktop.Services;

namespace Tickbird.Desktop.Views;

/// <summary>
/// Create / edit / remove / connect. The list is grouped by database type first (when more than one is in use) and by
/// group second; see <see cref="ConnectionListLayout"/>.
/// </summary>
public sealed class ManageConnectionsWindow : Window
{
    private static readonly IBrush Ink = Brush.Parse("#17233F");
    private static readonly IBrush Accent = Brush.Parse("#1a65a7");
    private static readonly IBrush Muted = Brush.Parse("#7284A5");
    private static readonly IBrush SelectedBack = Brush.Parse("#dfe9f3");
    private static readonly IBrush HoverBack = Brush.Parse("#F1F5FA");

    private readonly TickbirdApp app;
    private readonly TextBox search = new() { Name = "ConnectionSearch", Watermark = "Search by name, host or group", Height = 36, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly StackPanel list = new() { Name = "ConnectionList" };
    private readonly ScrollViewer scroll = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private readonly Button connect = new() { Name = "ConnectButton", Content = "Connect", IsEnabled = false, MinWidth = 80 };
    private readonly Button edit = new() { Name = "EditButton", Content = "Edit", IsEnabled = false, MinWidth = 80 };
    private readonly Button remove = new() { Name = "RemoveButton", Content = "Remove", IsEnabled = false, MinWidth = 80 };
    private readonly List<(DatabaseConnection Connection, Border Row)> rows = new();
    private DatabaseConnection? selected;

    public event Action<DatabaseConnection>? ConnectionCreated;
    public event Action<DatabaseConnection>? ConnectionUpdated;
    public event Action<DatabaseConnection>? ConnectionRemoved;

    public ManageConnectionsWindow(TickbirdApp app)
    {
        this.app = app;
        Title = "Manage Connections";
        Width = 720;
        Height = 480;
        MinWidth = 560;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Classes.Add("manageConnections");

        edit.Click += async (_, _) => await Guard(EditAsync);
        remove.Click += async (_, _) => await Guard(RemoveAsync);
        connect.Click += (_, _) => Close(selected);
        var close = new Button { Content = "Close", MinWidth = 80, IsCancel = true };
        close.Click += (_, _) => Close(null);

        StyleAction(edit, "Edit", "M4,17 L17,4 L21,8 L8,21 L3,22 Z M14,7 L18,11");
        StyleAction(remove, "Remove", "M4,6 L20,6 M9,6 L9,3 L15,3 L15,6 M6,6 L7,22 L17,22 L18,6 M10,10 L10,18 M14,10 L14,18");
        StyleAction(connect, "Connect", "M8,3 L8,8 M16,3 L16,8 M5,8 L19,8 L19,11 C19,15 16,18 12,18 C8,18 5,15 5,11 Z M12,18 L12,23");
        StyleAction(close, "Close", "M6,6 L18,18 M18,6 L6,18");
        var add = new Button { Name = "NewButton" };
        StyleAction(add, "New", "M12,3 L12,21 M3,12 L21,12");
        add.Click += async (_, _) => await Guard(CreateAsync);
        var groups = new Button { Name = "GroupsButton" };
        StyleAction(groups, "Groups", "M2,6 C2,5 3,4 4,4 L9,4 L11,7 L22,7 C23,7 24,8 24,9 L24,20 C24,21 23,22 22,22 L4,22 C3,22 2,21 2,20 Z M2,11 L24,11");
        ToolTip.SetTip(groups, "Create, rename or delete groups such as PROD or DEV");
        groups.Click += async (_, _) => await Guard(ManageGroupsAsync);
        add.Classes.Add("connectionPrimary");
        connect.Classes.Add("connectionConnect");
        remove.Classes.Add("connectionDanger");
        search.TextChanged += (_, _) => Redraw();

        scroll.Content = list;
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 10) };
        top.Children.Add(search);
        Grid.SetColumn(add, 1);
        top.Children.Add(add);

        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { edit, remove, groups } };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { close, connect } };
        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(right, Dock.Right);
        bottom.Children.Add(right);
        bottom.Children.Add(left);

        var layout = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        layout.Children.Add(top);
        layout.Children.Add(bottom);
        layout.Children.Add(new Border
        {
            BorderBrush = Brush.Parse("#E0E3E8"), BorderThickness = new Thickness(1), Background = Brushes.White,
            CornerRadius = new CornerRadius(5), ClipToBounds = true, Child = scroll
        });
        var card = TickbirdTheme.Card(layout);
        card.Padding = new Thickness(16, 14);
        card.ClipToBounds = true;
        Content = card;

        KeyDown += OnKeyDown;
        Redraw();
        Opened += (_, _) => search.Focus();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var inSearch = search.IsFocused;
        switch (e.Key)
        {
            case Key.Down: Move(1); e.Handled = true; break;
            case Key.Up: Move(-1); e.Handled = true; break;
            case Key.Enter when connect.IsEnabled: Close(selected); e.Handled = true; break;
            case Key.F2 when edit.IsEnabled: _ = Guard(EditAsync); e.Handled = true; break;
            case Key.Delete when !inSearch && remove.IsEnabled: _ = Guard(RemoveAsync); e.Handled = true; break;
        }
    }

    private void Move(int step)
    {
        if (rows.Count == 0) return;
        var index = rows.FindIndex(r => r.Connection == selected);
        Select(rows[Math.Clamp(index + step, 0, rows.Count - 1)].Connection);
    }

    private static string Code(DatabaseKind kind) => kind switch
    {
        DatabaseKind.InfluxDb1 => "IX1",
        DatabaseKind.InfluxDb3 => "IX3",
        DatabaseKind.TimescaleDb => "TS",
        DatabaseKind.CrateDb => "CR",
        _ => kind.ToString()[..Math.Min(2, kind.ToString().Length)].ToUpperInvariant()
    };

    private static IBrush BadgeColor(DatabaseKind kind) => Brush.Parse(kind switch
    {
        DatabaseKind.InfluxDb1 => "#1a65a7",
        DatabaseKind.InfluxDb3 => "#6B4FBB",
        DatabaseKind.TimescaleDb => "#B7791F",
        DatabaseKind.CrateDb => "#0F8A6B",
        _ => "#5A6B85"
    });

    private static void StyleAction(Button button, string label, string iconPath)
    {
        button.Height = 34;
        button.VerticalAlignment = VerticalAlignment.Center;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(iconPath), StrokeThickness = 1.7,
            Width = 15, Height = 15, Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center
        };
        icon.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty, new Binding("Foreground") { Source = button });
        content.Children.Add(icon);
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        button.Content = content;
        button.Classes.Add("connectionAction");
    }

    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { await Dialogs.ShowExceptionAsync(this, ex); }
    }

    private async Task ManageGroupsAsync()
    {
        await new ManageGroupsWindow(app.Settings, app.Save).ShowAsync(this);
        Redraw();
    }

    private Control Heading(ConnectionListEntry entry)
    {
        var isType = entry.Kind == ConnectionListEntryKind.TypeHeading;
        var text = new TextBlock
        {
            Text = isType ? entry.Title : entry.Title.ToUpperInvariant(),
            FontSize = isType ? 13 : 11, FontWeight = FontWeight.SemiBold, Foreground = isType ? Ink : Muted,
            VerticalAlignment = VerticalAlignment.Center
        };
        var count = new TextBlock { Text = entry.Count.ToString(), FontSize = 11, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center };
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { text, count } };
        if (isType)
        {
            var badge = new Border
            {
                Background = BadgeColor(Kind(entry)), CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 1),
                Child = new TextBlock { Text = Code(Kind(entry)), FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Brushes.White }
            };
            line.Children.Insert(0, badge);
        }
        return new Border
        {
            Classes = { isType ? "connectionTypeHeading" : "connectionGroupHeading" },
            Background = isType ? Brush.Parse("#E6ECF4") : Brushes.Transparent,
            Padding = isType ? new Thickness(10, 6) : new Thickness(26, 8, 10, 2),
            Child = line
        };
    }

    private static DatabaseKind Kind(ConnectionListEntry entry) => entry.Database ?? DatabaseKind.InfluxDb1;

    private Border Row(DatabaseConnection c, bool indented)
    {
        var badge = new Border
        {
            Background = BadgeColor(c.Kind), CornerRadius = new CornerRadius(4), Width = 32, Height = 20, VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = Code(c.Kind), FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        var name = new TextBlock { Text = c.Name, FontWeight = FontWeight.SemiBold, Foreground = Accent, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var host = new TextBlock { Text = $"{c.Host}:{c.Port}", FontSize = 12, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260 };
        var open = app.IsConnected(c);
        var status = new TextBlock { Name = "OpenMarker", Text = open ? "● open" : "", FontSize = 11, Foreground = Brush.Parse("#0F6E3A"), VerticalAlignment = VerticalAlignment.Center, MinWidth = 46 };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 12 };
        grid.Children.Add(badge);
        Grid.SetColumn(name, 1); grid.Children.Add(name);
        Grid.SetColumn(host, 2); grid.Children.Add(host);
        Grid.SetColumn(status, 3); grid.Children.Add(status);

        var row = new Border
        {
            Classes = { "connectionRow" }, Tag = c, Background = Brushes.Transparent, Padding = new Thickness(indented ? 26 : 10, 7, 10, 7),
            BorderThickness = new Thickness(3, 0, 0, 0), BorderBrush = Brushes.Transparent, Child = grid
        };
        row.PointerEntered += (_, _) => { if (selected != c) row.Background = HoverBack; };
        row.PointerExited += (_, _) => { if (selected != c) row.Background = Brushes.Transparent; };
        row.PointerPressed += (_, _) => Select(c);
        row.DoubleTapped += (_, _) => { if (connect.IsEnabled) Close(selected); };
        return row;
    }

    private void Select(DatabaseConnection? connection)
    {
        selected = connection;
        foreach (var (c, row) in rows)
        {
            var on = c == connection;
            row.Background = on ? SelectedBack : Brushes.Transparent;
            row.BorderBrush = on ? Accent : Brushes.Transparent;
            if (on) row.BringIntoView();
        }
        edit.IsEnabled = remove.IsEnabled = connection != null;
        connect.IsEnabled = connection != null && !app.IsConnected(connection);
    }

    public void Redraw()
    {
        var previous = selected;
        var entries = ConnectionListLayout.Build(app.Settings.Connections, search.Text);
        list.Children.Clear();
        rows.Clear();
        // A connection is indented when it sits under a group heading.
        var underGroup = false;
        foreach (var entry in entries)
        {
            switch (entry.Kind)
            {
                case ConnectionListEntryKind.TypeHeading: underGroup = false; list.Children.Add(Heading(entry)); break;
                case ConnectionListEntryKind.GroupHeading: underGroup = true; list.Children.Add(Heading(entry)); break;
                default:
                    var row = Row(entry.Connection!, underGroup);
                    rows.Add((entry.Connection!, row));
                    list.Children.Add(row);
                    break;
            }
        }
        if (rows.Count == 0)
        {
            list.Children.Add(new TextBlock
            {
                Name = "ConnectionListEmpty", Foreground = Muted, Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap,
                Text = app.Settings.Connections.Count == 0 ? "No connections yet. Click New to add one." : "No connection matches the search."
            });
        }
        Select(rows.Select(r => r.Connection).FirstOrDefault(c => c == previous) ?? rows.Select(r => r.Connection).FirstOrDefault());
    }

    private async Task CreateAsync()
    {
        // A new connection starts in the group of the one that is selected.
        var group = TickbirdSettings.NormalizeGroup(selected?.Group);
        var connection = await TickbirdDialogs.ConnectionAsync(this, null, app.Settings, group);
        if (connection == null) return;
        connection.Group = app.Settings.EnsureGroup(connection.Group);
        app.Settings.Connections.Add(connection);
        app.Save();
        ConnectionCreated?.Invoke(connection);
        search.Text = "";
        selected = connection;
        Redraw();
    }

    private async Task EditAsync()
    {
        var existing = selected;
        if (existing == null) return;
        var updated = await TickbirdDialogs.ConnectionAsync(this, existing, app.Settings);
        if (updated == null) return;
        existing.Name = updated.Name; existing.Host = updated.Host; existing.Port = updated.Port;
        existing.Database = updated.Database; existing.Username = updated.Username; existing.Password = updated.Password;
        existing.UseSsl = updated.UseSsl; existing.ServerVersion = updated.ServerVersion; existing.Kind = updated.Kind; existing.Token = updated.Token;
        existing.Group = app.Settings.EnsureGroup(updated.Group);
        app.Save();
        ConnectionUpdated?.Invoke(existing);
        Redraw();
    }

    private async Task RemoveAsync()
    {
        var connection = selected;
        if (connection == null) return;
        if (!await Dialogs.ConfirmAsync(this, "Confirm Delete", $"Are you sure you want to delete the connection \"{connection.Name}\"?")) return;
        // Select the next connection in the list after the removed one.
        var index = rows.FindIndex(r => r.Connection == connection);
        app.Settings.Connections.RemoveAll(c => c.Id == connection.Id);
        app.Save();
        ConnectionRemoved?.Invoke(connection);
        selected = null;
        Redraw();
        if (rows.Count > 0) Select(rows[Math.Min(Math.Max(index, 0), rows.Count - 1)].Connection);
    }

    /// <summary>Shows the dialog; resolves to the connection the user chose to connect to, if any.</summary>
    public async Task<DatabaseConnection?> ShowAsync(Window owner) => await ShowDialog<DatabaseConnection?>(owner);
}
