using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Tickbird.Core.Navigation;

namespace Tickbird.Desktop.Services;

/// <summary>Small modal dialog helpers (message box, confirmation, simple forms).</summary>
public static class Dialogs
{
    public static Task ShowMessageAsync(Window owner, string title, string message) =>
        new MessageWindow(title, message, ok: "OK", cancel: null).ShowDialog<bool>(owner);

    public static Task ShowErrorAsync(Window owner, string message, string title = "Error") =>
        ShowMessageAsync(owner, title, message);

    public static Task ShowExceptionAsync(Window owner, Exception ex, string title = "Unexpected Error") =>
        ShowMessageAsync(owner, title, ex.Message);

    public static async Task<bool> ConfirmAsync(Window owner, string title, string message) =>
        await new MessageWindow(title, message, ok: "OK", cancel: "Cancel").ShowDialog<bool>(owner);

    /// <summary>
    /// A confirmation with a check box under the message (unchecked at first). <paramref name="option"/> is null to
    /// leave the check box out. Returns whether the user confirmed and whether the box was checked.
    /// </summary>
    public static async Task<(bool Confirmed, bool Checked)> ConfirmWithOptionAsync(
        Window owner, string title, string message, string? option, string ok = "Delete")
    {
        var window = new OptionConfirmWindow(title, message, option, ok);
        var confirmed = await window.ShowDialog<bool>(owner);
        return (confirmed, confirmed && window.OptionBox.IsChecked == true);
    }

    /// <summary>
    /// Confirmation for destructive actions: the user must type <paramref name="name"/> exactly before
    /// the action button is enabled. Returns true only when confirmed.
    /// </summary>
    public static async Task<bool> ConfirmByNameAsync(Window owner, string title, string message, string name, string action = "Drop") =>
        await new ConfirmByNameWindow(title, message, name, action).ShowDialog<bool>(owner);

    /// <summary>Asks for one line of text. Returns null when cancelled, otherwise the text (possibly empty).</summary>
    public static async Task<string?> PromptTextAsync(Window owner, string title, string message, string? initial = null, string ok = "OK")
    {
        var input = new TextBox { Text = initial ?? "", MaxLength = 200, MinWidth = 340 };
        var confirm = new Button { Content = ok, MinWidth = 80, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
        var window = new Window
        {
            Title = title, Width = 440, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        confirm.Click += (_, _) => window.Close(true);
        cancel.Click += (_, _) => window.Close(false);
        var content = new StackPanel { Margin = new Thickness(4), Spacing = 12 };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(input);
        content.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
            Children = { confirm, cancel }
        });
        window.Content = TickbirdTheme.Card(content);
        window.Opened += (_, _) => { input.Focus(); input.SelectAll(); };
        return await window.ShowDialog<bool>(owner) ? input.Text ?? "" : null;
    }

    /// <summary>
    /// Asks for a value for each query parameter. <paramref name="remembered"/> pre-fills the boxes. Returns null
    /// when cancelled.
    /// </summary>
    public static async Task<Dictionary<string, string>?> PromptParametersAsync(
        Window owner, IReadOnlyList<string> names, IReadOnlyDictionary<string, string> remembered)
    {
        var boxes = names.ToDictionary(n => n, n => new TextBox { Text = remembered.TryGetValue(n, out var v) ? v : "", MinWidth = 260 });
        var confirm = new Button { Content = "Run", MinWidth = 80, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
        var window = new Window
        {
            Title = "Query Parameters", Width = 460, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        confirm.Click += (_, _) => window.Close(true);
        cancel.Click += (_, _) => window.Close(false);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowSpacing = 8, ColumnSpacing = 10 };
        var row = 0;
        foreach (var name in names)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(label, row);
            Grid.SetRow(boxes[name], row);
            Grid.SetColumn(boxes[name], 1);
            grid.Children.Add(label);
            grid.Children.Add(boxes[name]);
            row++;
        }
        var content = new StackPanel { Margin = new Thickness(4), Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = "The query has parameters. The text you enter replaces {{name}} exactly as typed, so add quotes for text values.",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(grid);
        content.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
            Children = { confirm, cancel }
        });
        window.Content = TickbirdTheme.Card(content);
        window.Opened += (_, _) => boxes[names[0]].Focus();
        return await window.ShowDialog<bool>(owner) ? boxes.ToDictionary(p => p.Key, p => p.Value.Text ?? "") : null;
    }

    public enum SaveChoice { Save, DontSave, Cancel }

    /// <summary>Asks what to do with unsaved changes: Save, Don't Save or Cancel.</summary>
    public static async Task<SaveChoice> AskSaveChangesAsync(Window owner, string message)
    {
        var window = new Window
        {
            Title = "Unsaved Changes", Width = 440, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var choice = SaveChoice.Cancel;
        Button Make(string text, SaveChoice c, bool def = false, bool cancel = false)
        {
            var b = new Button { Content = text, MinWidth = 90, IsDefault = def, IsCancel = cancel };
            b.Click += (_, _) => { choice = c; window.Close(); };
            return b;
        }
        window.Content = TickbirdTheme.Card(new StackPanel
        {
            Margin = new Thickness(4), Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
                    Children = { Make("Save", SaveChoice.Save, def: true), Make("Don't Save", SaveChoice.DontSave), Make("Cancel", SaveChoice.Cancel, cancel: true) }
                }
            }
        });
        await window.ShowDialog(owner);
        return choice;
    }

    /// <summary>The window behind <see cref="ConfirmWithOptionAsync"/>.</summary>
    public sealed class OptionConfirmWindow : Window
    {
        public CheckBox OptionBox { get; } = new();
        public Button ConfirmButton { get; } = new() { MinWidth = 80, IsDefault = true };

        public OptionConfirmWindow(string title, string message, string? option, string ok)
        {
            Title = title;
            Width = 440;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ConfirmButton.Content = ok;
            ConfirmButton.Click += (_, _) => Close(true);
            var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
            cancel.Click += (_, _) => Close(false);
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
                Children = { ConfirmButton, cancel }
            };
            var content = new StackPanel { Margin = new Thickness(4), Spacing = 14 };
            content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
            if (option != null)
            {
                OptionBox.Content = option;
                content.Children.Add(OptionBox);
            }
            content.Children.Add(buttons);
            Content = TickbirdTheme.Card(content);
        }
    }

    private sealed class MessageWindow : Window
    {
        public MessageWindow(string title, string message, string ok, string? cancel)
        {
            Title = title;
            Width = 420;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            var okButton = new Button { Content = ok, MinWidth = 80, IsDefault = true };
            okButton.Click += (_, _) => Close(true);
            buttons.Children.Add(okButton);
            if (cancel != null)
            {
                var cancelButton = new Button { Content = cancel, MinWidth = 80, IsCancel = true };
                cancelButton.Click += (_, _) => Close(false);
                buttons.Children.Add(cancelButton);
            }
            Content = TickbirdTheme.Card(new StackPanel
            {
                Margin = new Thickness(4),
                Spacing = 16,
                Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, buttons }
            });
        }
    }
}

/// <summary>A label/control form with OK/Cancel and optional validation (shown inline).</summary>
public sealed class FormDialog : Window
{
    private readonly TextBlock errorText = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button okButton;
    private readonly Grid footer;

    /// <summary>Extra buttons shown at the left of the footer (for example Test / Ping).</summary>
    public StackPanel LeftButtons { get; } = new() { Orientation = Orientation.Horizontal, Spacing = 8 };

    public FormDialog(string title, string okText, params (string Label, Control Control)[] rows)
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowSpacing = 8, ColumnSpacing = 12 };
        foreach (var (label, control) in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var row = grid.RowDefinitions.Count - 1;
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(text, row);
            control.HorizontalAlignment = HorizontalAlignment.Stretch;
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            grid.Children.Add(text);
            grid.Children.Add(control);
        }

        var rightButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        Grid.SetColumn(rightButtons, 1);
        footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { LeftButtons, rightButtons } };
        okButton = new Button { Content = okText, MinWidth = 80, IsDefault = true };
        okButton.Click += (_, _) =>
        {
            var error = Validate?.Invoke();
            if (error != null) { errorText.Text = error; errorText.IsVisible = true; return; }
            Close(true);
        };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
        cancel.Click += (_, _) => Close(false);
        rightButtons.Children.Add(okButton);
        rightButtons.Children.Add(cancel);

        Content = TickbirdTheme.Card(new StackPanel
        {
            Margin = new Thickness(4),
            Spacing = 12,
            Children =
            {
                grid,
                errorText,
                footer
            }
        });
    }

    /// <summary>Returns an error message to keep the dialog open, or null when the input is valid.</summary>
    public Func<string?>? Validate { get; set; }

    /// <summary>Replaces only the form presentation, retaining validation and footer actions.</summary>
    internal void SetBody(Control body)
    {
        if (errorText.Parent is Panel errorParent) errorParent.Children.Remove(errorText);
        if (footer.Parent is Panel footerParent) footerParent.Children.Remove(footer);
        Content = new StackPanel
        {
            Children =
            {
                body,
                new Border { Padding = new Thickness(24, 0, 24, 12), Child = errorText },
                new Border
                {
                    BorderBrush = TickbirdTheme.Line, BorderThickness = new Thickness(0, 1, 0, 0),
                    Padding = new Thickness(24, 16), Child = footer
                }
            }
        };
    }

    public async Task<bool> ShowAsync(Window owner) => await ShowDialog<bool>(owner);
}

public static class Ui
{
    public static TextBox Text(string? value = null, bool password = false, string? watermark = null) =>
        new() { Text = value, PasswordChar = password ? '•' : '\0', Watermark = watermark };

    public static NumericUpDown Number(decimal value, decimal min, decimal max) =>
        new() { Value = value, Minimum = min, Maximum = max, Increment = 1, FormatString = "0" };
}

/// <summary>Type-the-name confirmation used before dropping a database, measurement or series.</summary>
public sealed class ConfirmByNameWindow : Window
{
    public ConfirmByNameWindow(string title, string message, string name, string action)
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        ConfirmButton = new Button
        {
            Content = action, MinWidth = 80, IsEnabled = false,
            Background = Brush.Parse("#C62828"), Foreground = Brushes.White
        };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
        bool Matches() => Validation.NameMatches(Input.Text, name);
        // React to the Text property directly so the button state is always in step with what was typed.
        Input.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) ConfirmButton.IsEnabled = Matches();
        };
        Input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || !Matches()) return;
            e.Handled = true;
            Close(true);
        };
        ConfirmButton.Click += (_, _) => { if (Matches()) Close(true); };
        cancel.Click += (_, _) => Close(false);
        Opened += (_, _) => Input.Focus();

        var prompt = new TextBlock { TextWrapping = TextWrapping.Wrap };
        prompt.Inlines = new InlineCollection
        {
            new Run("To confirm, type "),
            new Run(name) { FontWeight = FontWeight.Bold },
            new Run(" below:")
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
            Children = { ConfirmButton, cancel }
        };
        Content = TickbirdTheme.Card(new StackPanel
        {
            Margin = new Thickness(4),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "This cannot be undone.", Foreground = Brush.Parse("#C62828"), FontWeight = FontWeight.SemiBold },
                prompt,
                Input,
                buttons
            }
        });
    }

    /// <summary>Where the user types the name.</summary>
    public TextBox Input { get; } = new();

    /// <summary>Enabled only while the typed text matches the name exactly.</summary>
    public Button ConfirmButton { get; }
}
