using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Tickbird.Core.Settings;
using Tickbird.Desktop.Services;

namespace Tickbird.Desktop.Views;

/// <summary>Create, rename and delete connection groups (for example PROD, DEV).</summary>
public sealed class ManageGroupsWindow : Window
{
    private sealed record GroupItem(string Name, int Count, string? Color)
    {
        public override string ToString() => Count == 1 ? $"{Name}   (1 connection)" : $"{Name}   ({Count} connections)";
    }

    private readonly Button colour = new() { Name = "GroupColourButton", MinWidth = 90, IsEnabled = false };

    private readonly TickbirdSettings settings;
    private readonly Action save;
    private readonly ListBox list = new() { MinHeight = 200, Background = Brushes.White };
    private readonly Button rename = new() { MinWidth = 90, IsEnabled = false };
    private readonly Button delete = new() { MinWidth = 90, IsEnabled = false };

    public ManageGroupsWindow(TickbirdSettings settings, Action save)
    {
        this.settings = settings;
        this.save = save;
        Title = "Connection Groups";
        Width = 460;
        Height = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // Same look as the Manage Connections window.
        Classes.Add("manageConnections");
        var add = new Button { MinWidth = 110 };
        var close = new Button { MinWidth = 80, IsCancel = true };
        Action(add, "New Group", "M12,3 L12,21 M3,12 L21,12");
        Action(rename, "Rename", "M4,17 L17,4 L21,8 L8,21 L3,22 Z M14,7 L18,11");
        Action(delete, "Delete", "M4,6 L20,6 M9,6 L9,3 L15,3 L15,6 M6,6 L7,22 L17,22 L18,6 M10,10 L10,18 M14,10 L14,18");
        Action(colour, "Colour", "M12,3 C7,3 3,7 3,12 C3,17 7,21 12,21 C14,21 14,19 13,18 C12,16 14,15 16,15 L18,15 C20,15 21,14 21,12 C21,7 17,3 12,3 Z");
        colour.Click += async (_, _) => await Guard(ColourAsync);
        list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<GroupItem>((item, _) =>
        {
            var dot = new Border
            {
                Width = 12, Height = 12, CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Center,
                Background = item?.Color != null ? Brush.Parse(item.Color) : Brushes.Transparent,
                BorderBrush = Brush.Parse("#C9D3E3"), BorderThickness = new Thickness(item?.Color != null ? 0 : 1)
            };
            return new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 10,
                Children = { dot, new TextBlock { Text = item?.ToString() ?? "", VerticalAlignment = VerticalAlignment.Center } }
            };
        });
        Action(close, "Close", "M6,6 L18,18 M18,6 L6,18");
        add.Classes.Add("connectionPrimary");
        delete.Classes.Add("connectionDanger");
        add.Click += async (_, _) => await Guard(AddAsync);
        rename.Click += async (_, _) => await Guard(RenameAsync);
        delete.Click += async (_, _) => await Guard(DeleteAsync);
        close.Click += (_, _) => Close();
        list.SelectionChanged += (_, _) => rename.IsEnabled = delete.IsEnabled = colour.IsEnabled = list.SelectedItem != null;
        list.DoubleTapped += async (_, _) => await Guard(RenameAsync);

        var hint = new TextBlock
        {
            Text = "Use groups to organise servers, for example Prod or Dev. " +
                   "Choose a connection's group in its Create / Edit dialog.",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.85
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 12), Children = { add, rename, colour, delete } };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Children = { close } };
        var layout = new DockPanel();
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(hint);
        layout.Children.Add(buttons);
        layout.Children.Add(footer);
        layout.Children.Add(new Border
        {
            BorderBrush = Brush.Parse("#E0E3E8"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5), ClipToBounds = true, Child = list
        });
        var card = TickbirdTheme.Card(layout);
        card.Padding = new Thickness(18, 14);
        Content = card;
        Redraw(null);
    }

    private static void Action(Button button, string label, string iconPath)
    {
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(iconPath), StrokeThickness = 1.7, Width = 15, Height = 15,
            Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center
        };
        icon.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty, new Avalonia.Data.Binding("Foreground") { Source = button });
        button.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 7,
            Children = { icon, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center } }
        };
        button.Classes.Add("connectionAction");
    }

    private string? SelectedName => (list.SelectedItem as GroupItem)?.Name;

    private void Redraw(string? select)
    {
        var items = settings.AllGroups().Select(g => new GroupItem(g, settings.CountInGroup(g), settings.GroupColor(g))).ToList();
        list.ItemsSource = items;
        list.SelectedItem = items.FirstOrDefault(i => TickbirdSettings.SameGroup(i.Name, select)) ?? items.FirstOrDefault();
        rename.IsEnabled = delete.IsEnabled = colour.IsEnabled = list.SelectedItem != null;
    }

    private async Task ColourAsync()
    {
        var group = SelectedName;
        if (group == null) return;
        var (saved, color) = await TickbirdDialogs.ColorAsync(this, $"Colour of {group}", settings.GroupColor(group));
        if (!saved) return;
        settings.SetGroupColor(group, color);
        save();
        Redraw(group);
    }

    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { await Dialogs.ShowExceptionAsync(this, ex); }
    }

    private async Task AddAsync()
    {
        var name = await TickbirdDialogs.GroupNameAsync(this, "New Group", "Create", null, settings.AllGroups());
        if (name == null) return;
        settings.EnsureGroup(name);
        save();
        Redraw(name);
    }

    private async Task RenameAsync()
    {
        var current = SelectedName;
        if (current == null) return;
        var name = await TickbirdDialogs.GroupNameAsync(this, "Rename Group", "Rename", current, settings.AllGroups());
        if (name == null || name == current) return;
        settings.RenameGroup(current, name);
        save();
        Redraw(name);
    }

    private async Task DeleteAsync()
    {
        var group = SelectedName;
        if (group == null) return;
        var count = settings.CountInGroup(group);
        var message = count == 0
            ? $"Delete the group \"{group}\"?"
            : $"Delete the group \"{group}\"? Its {count} connection(s) will not be deleted; they will move to {TickbirdDialogs.NoGroup}.";
        if (!await Dialogs.ConfirmAsync(this, "Delete Group", message)) return;
        settings.RemoveGroup(group);
        save();
        Redraw(null);
    }

    public Task ShowAsync(Window owner) => ShowDialog(owner);
}
