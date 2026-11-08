using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Tickbird.Desktop.Services;

/// <summary>One step of the Getting Started guide.</summary>
public sealed record GuideStep(string Title, string Hint, Func<bool> IsDone, string? ActionText = null, Func<Task>? Action = null, Func<bool>? CanAct = null);

/// <summary>
/// A small window that stays beside the app and walks through the first steps: add a connection, connect, open a
/// query and run it. Each step is ticked off as soon as it is done, the next one is highlighted and has a button
/// that does it.
/// </summary>
public sealed class GettingStartedWindow : Window
{
    private static readonly IBrush Accent = Brush.Parse("#1a65a7");
    private static readonly IBrush Done = Brush.Parse("#2e8b57");
    private static readonly IBrush Muted = Brush.Parse("#6b7f90");

    private readonly IReadOnlyList<GuideStep> steps;
    private readonly List<Row> rows = new();
    private readonly TextBlock footer = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    private sealed record Row(Border Card, TextBlock Mark, TextBlock Title, TextBlock Hint, Button? Button);

    /// <summary>The check box "Show this guide when Tickbird starts".</summary>
    public CheckBox ShowAtStartup { get; } = new() { Content = "Show this guide when Tickbird starts", FontSize = 12 };

    public GettingStartedWindow(IReadOnlyList<GuideStep> steps, bool showAtStartup)
    {
        this.steps = steps;
        Title = "Getting Started";
        Width = 400;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowAtStartup.IsChecked = showAtStartup;

        var list = new StackPanel { Spacing = 8 };
        list.Children.Add(new TextBlock
        {
            Text = "Connect to a server and run your first query", FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Accent,
            TextWrapping = TextWrapping.Wrap
        });
        foreach (var step in steps)
        {
            var mark = new TextBlock { FontSize = 16, Width = 22, VerticalAlignment = VerticalAlignment.Top };
            var title = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
            var hint = new TextBlock { Text = step.Hint, FontSize = 12, Foreground = Muted, TextWrapping = TextWrapping.Wrap };
            title.Text = step.Title;
            Button? button = null;
            var text = new StackPanel { Spacing = 3, Children = { title, hint } };
            if (step.Action != null)
            {
                button = new Button { Content = step.ActionText ?? "Do it", Padding = new Thickness(10, 3), FontSize = 12, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
                var action = step.Action;
                button.Click += async (_, _) => await action();
                text.Children.Add(button);
            }
            var card = new Border
            {
                Padding = new Thickness(10), CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1),
                Child = new DockPanel { LastChildFill = true, Children = { Dock(mark), text } }
            };
            rows.Add(new Row(card, mark, title, hint, button));
            list.Children.Add(card);
        }
        list.Children.Add(footer);
        list.Children.Add(ShowAtStartup);
        var close = new Button { Content = "Close", MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right, IsCancel = true };
        close.Click += (_, _) => Close();
        list.Children.Add(close);
        Content = TickbirdTheme.Card(list);

        timer.Tick += (_, _) => Refresh();
        Opened += (_, _) => { Refresh(); timer.Start(); };
        Closed += (_, _) => timer.Stop();
    }

    private static Control Dock(Control c)
    {
        DockPanel.SetDock(c, Avalonia.Controls.Dock.Left);
        return c;
    }

    /// <summary>Ticks off finished steps and highlights the first one that is not (exposed for tests).</summary>
    public void Refresh()
    {
        var current = -1;
        for (var i = 0; i < steps.Count; i++)
        {
            var done = steps[i].IsDone();
            if (!done && current < 0) current = i;
            var row = rows[i];
            row.Mark.Text = done ? "✔" : (i == current ? "▶" : "○");
            row.Mark.Foreground = done ? Done : (i == current ? Accent : Muted);
            row.Title.Foreground = done ? Muted : Brushes.Black;
            row.Card.Background = i == current ? Brush.Parse("#EAF3FC") : Brushes.White;
            row.Card.BorderBrush = i == current ? Accent : Brush.Parse("#e5eef5");
            if (row.Button != null) row.Button.IsEnabled = !done && (steps[i].CanAct?.Invoke() ?? true);
        }
        footer.Text = current < 0
            ? "You're all set. Open Help › Getting Started any time to see this guide again."
            : $"Step {current + 1} of {steps.Count}";
        footer.Foreground = current < 0 ? Done : Muted;
    }
}
