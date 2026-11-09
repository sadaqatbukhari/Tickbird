using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Tickbird.Core.Data;
using Tickbird.Desktop.Services;

namespace Tickbird.Desktop.Views;

/// <summary>A form with one box per column, to type a new record or change an existing one. An empty box is NULL (or left out when inserting).</summary>
public sealed class RecordEditorWindow : Window
{
    private readonly Dictionary<string, TextBox> boxes = new(StringComparer.OrdinalIgnoreCase);
    private bool accepted;

    public RecordEditorWindow(string title, string intro, RecordSchema schema, IEnumerable<string> columns,
        IReadOnlyDictionary<string, object>? original, string okText)
    {
        Title = title;
        Width = 560;
        Height = 560;
        MinWidth = 420;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Classes.Add("manageConnections");

        var fields = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*"), ColumnSpacing = 12, RowSpacing = 8 };
        var wanted = new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase);
        foreach (var column in schema.Columns.Where(c => wanted.Contains(c.Name)))
        {
            var row = fields.RowDefinitions.Count;
            fields.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var fixedKey = original != null && schema.KeyIsFixed && column.IsKey;
            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children =
            {
                new TextBlock { Text = column.Name, FontWeight = column.IsKey ? FontWeight.SemiBold : FontWeight.Normal, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = Describe(column), FontSize = 10.5, Foreground = Brush.Parse("#7284A5") }
            } };
            var value = original != null && original.TryGetValue(column.Name, out var existing) ? RecordValues.ToText(existing) : "";
            var box = new TextBox
            {
                Name = "Field_" + column.Name, Text = value, IsReadOnly = fixedKey, Watermark = original == null ? "empty" : "NULL",
                MinHeight = 30, AcceptsReturn = false
            };
            if (fixedKey)
            {
                box.Opacity = 0.65;
                ToolTip.SetTip(box, "The time and tags identify a point, so they cannot be changed. Insert a new point and delete this one instead.");
            }
            Grid.SetRow(label, row);
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            fields.Children.Add(label);
            fields.Children.Add(box);
            boxes[column.Name] = box;
        }

        var ok = new Button { Name = "RecordOk", Content = okText, MinWidth = 90, IsDefault = true };
        ok.Classes.Add("connectionPrimary");
        var cancel = new Button { Name = "RecordCancel", Content = "Cancel", MinWidth = 90, IsCancel = true };
        ok.Click += (_, _) => { accepted = true; Close(true); };
        cancel.Click += (_, _) => Close(false);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Children = { cancel, ok } };
        var head = new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), Opacity = 0.9 };

        var layout = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(head);
        layout.Children.Add(buttons);
        layout.Children.Add(new ScrollViewer { Content = fields, Padding = new Thickness(0, 0, 12, 0) });
        var card = TickbirdTheme.Card(layout);
        card.Padding = new Thickness(18, 14);
        Content = card;
    }

    private static string Describe(RecordColumn column) =>
        column.IsTime ? "time" : column.DataType == "tag" ? "tag" : column.DataType + (column.IsKey ? " · key" : "");

    /// <summary>What the user typed per column. An empty box is null.</summary>
    public IReadOnlyDictionary<string, string?> Values =>
        boxes.ToDictionary(p => p.Key, p => string.IsNullOrEmpty(p.Value.Text) ? null : p.Value.Text, StringComparer.OrdinalIgnoreCase);

    public bool Accepted => accepted;

    public async Task<bool> ShowAsync(Window owner) => await ShowDialog<bool>(owner);
}
