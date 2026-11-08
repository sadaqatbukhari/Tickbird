using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Tickbird.Core.Query;

namespace Tickbird.Desktop.Services;

/// <summary>Lists the saved snippets. Insert closes the window with the chosen query text; Close returns null.</summary>
public sealed class SnippetsWindow : Window
{
    private readonly QuerySnippets snippets;
    private readonly ListBox list = new() { MinHeight = 200 };
    private readonly TextBox preview = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 140, FontSize = 12,
        FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace")
    };
    private readonly Button insert = new() { Content = "Insert", MinWidth = 80, IsDefault = true, IsEnabled = false };
    private readonly Button rename = new() { Content = "Rename…", MinWidth = 80, IsEnabled = false };
    private readonly Button delete = new() { Content = "Delete", MinWidth = 80, IsEnabled = false };

    public SnippetsWindow(QuerySnippets snippets)
    {
        this.snippets = snippets;
        Title = "Query Snippets";
        Width = 560;
        Height = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var close = new Button { Content = "Close", MinWidth = 80, IsCancel = true };
        close.Click += (_, _) => Close(null);
        insert.Click += (_, _) => { if (Current() is { } s) Close(s.Query); };
        rename.Click += async (_, _) => await RenameAsync();
        delete.Click += async (_, _) => await DeleteAsync();
        list.DoubleTapped += (_, _) => { if (Current() is { } s) Close(s.Query); };
        list.SelectionChanged += (_, _) =>
        {
            var current = Current();
            preview.Text = current?.Query ?? "";
            insert.IsEnabled = rename.IsEnabled = delete.IsEnabled = current != null;
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
            Children = { insert, rename, delete, close }
        };
        var layout = new DockPanel { Margin = new Thickness(4) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(preview, Dock.Bottom);
        buttons.Margin = new Thickness(0, 10, 0, 0);
        preview.Margin = new Thickness(0, 10, 0, 0);
        layout.Children.Add(buttons);
        layout.Children.Add(preview);
        layout.Children.Add(list);
        Content = TickbirdTheme.Card(layout);
        Fill(null);
    }

    private QuerySnippet? Current() => list.SelectedItem as QuerySnippet;

    private void Fill(string? selectId)
    {
        var items = snippets.Items;
        list.ItemsSource = items;
        list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<QuerySnippet>(
            (s, _) => new TextBlock { Text = s?.Name ?? "", Margin = new Thickness(4, 2) });
        list.SelectedItem = items.FirstOrDefault(s => s.Id == selectId) ?? items.FirstOrDefault();
        if (items.Count == 0)
            preview.Text = "No snippets yet. Use Query › Save as Snippet to save the selected text or the whole editor.";
    }

    private async Task RenameAsync()
    {
        if (Current() is not { } snippet) return;
        var name = await Dialogs.PromptTextAsync(this, "Rename Snippet", "New name for the snippet.", snippet.Name, ok: "Rename");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!snippets.Rename(snippet.Id, name))
        {
            await Dialogs.ShowErrorAsync(this, "A snippet with that name already exists.", "Rename Snippet");
            return;
        }
        Fill(snippet.Id);
    }

    private async Task DeleteAsync()
    {
        if (Current() is not { } snippet) return;
        if (!await Dialogs.ConfirmAsync(this, "Delete Snippet", $"Delete the snippet \"{snippet.Name}\"? This cannot be undone.")) return;
        snippets.Remove(snippet.Id);
        Fill(null);
    }
}
