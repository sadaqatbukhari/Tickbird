using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Tickbird.Core.Providers;

namespace Tickbird.Desktop.Services;

/// <summary>The notes under the server fields and the result of Test connection, drawn from what the core library found.</summary>
internal static class ConnectionDialogPanels
{
    private static readonly IBrush Ink = Brush.Parse("#17233F");
    private static readonly IBrush Muted = Brush.Parse("#7284A5");

    private static (IBrush Back, IBrush Fore, string Mark) Look(AdviceSeverity severity) => severity switch
    {
        AdviceSeverity.Error => (Brush.Parse("#FEECEB"), Brush.Parse("#B42318"), "✘"),
        AdviceSeverity.Warning => (Brush.Parse("#FFF4DB"), Brush.Parse("#8A5A00"), "⚠"),
        _ => (Brush.Parse("#EAF3FD"), Brush.Parse("#176FC1"), "ⓘ")
    };

    private static Border Note(string text, AdviceSeverity severity, string? name = null)
    {
        var (back, fore, mark) = Look(severity);
        return new Border
        {
            Name = name, Background = back, CornerRadius = new CornerRadius(5), Padding = new Thickness(9, 5),
            Child = new TextBlock { Text = mark + "  " + text, FontSize = 11.5, Foreground = fore, TextWrapping = TextWrapping.Wrap }
        };
    }

    /// <summary>Shows the notes of <see cref="ConnectionAdvisor"/> (errors, warnings, information).</summary>
    public static void RenderAdvice(StackPanel panel, IEnumerable<ConnectionAdvice> advice)
    {
        panel.Children.Clear();
        foreach (var note in advice) panel.Children.Add(Note(note.Message, note.Severity));
        panel.IsVisible = panel.Children.Count > 0;
    }

    /// <summary>Shows that a test is running.</summary>
    public static void RenderBusy(StackPanel panel)
    {
        panel.Children.Clear();
        panel.Children.Add(new TextBlock { Name = "ConnectionTestBusy", Text = "Testing the connection…", FontSize = 12, Foreground = Muted });
        panel.IsVisible = true;
    }

    /// <summary>Shows a problem found before anything was tried (a blank host, the HTTP port).</summary>
    public static void RenderProblem(StackPanel panel, string problem)
    {
        panel.Children.Clear();
        panel.Children.Add(Note(problem, AdviceSeverity.Error, "ConnectionTestFailure"));
        panel.IsVisible = true;
    }

    /// <summary>
    /// A failed test shows every step with the failed one explained and a hint. A passing test shows one line, the
    /// databases found to pick from, and a Details link for the steps.
    /// </summary>
    public static void RenderReport(StackPanel panel, DiagnosticReport report, DatabaseProfile profile, TextBox database)
    {
        panel.Children.Clear();
        panel.IsVisible = true;
        var steps = new StackPanel { Name = "ConnectionTestSteps", Spacing = 3 };
        foreach (var step in report.Steps)
        {
            var (mark, color) = step.Status switch
            {
                StepStatus.Ok => ("✔", Brush.Parse("#0F6E3A")),
                StepStatus.Failed => ("✘", Brush.Parse("#B42318")),
                _ => ("○", Muted)
            };
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            line.Children.Add(new TextBlock { Text = mark, Foreground = color, FontSize = 12, Width = 14 });
            line.Children.Add(new TextBlock { Text = step.Name, FontSize = 12, Foreground = Ink, Width = 90 });
            line.Children.Add(new TextBlock
            {
                Text = step.Status == StepStatus.Ok && step.Elapsed > TimeSpan.Zero ? $"{step.Detail} · {step.Elapsed.TotalMilliseconds:0} ms" : step.Detail,
                FontSize = 12, Foreground = step.Status == StepStatus.Failed ? color : Muted
            });
            steps.Children.Add(line);
        }

        if (!report.Success)
        {
            panel.Children.Add(steps);
            if (!string.IsNullOrWhiteSpace(report.Hint)) panel.Children.Add(Note(report.Hint!, AdviceSeverity.Info, "ConnectionTestHint"));
            return;
        }

        var version = string.IsNullOrWhiteSpace(report.Version) ? profile.DisplayName : report.Version;
        panel.Children.Add(Note($"Connected to {version} in {report.Latency.TotalMilliseconds:0} ms.", AdviceSeverity.Info, "ConnectionTestSuccess"));
        if (profile.UsesDatabaseName && report.Databases.Count > 0)
        {
            var choices = new List<string> { "(all databases)" };
            choices.AddRange(report.Databases);
            var picker = new ComboBox
            {
                Name = "ConnectionTestDatabases", ItemsSource = choices, HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedIndex = Math.Max(0, choices.IndexOf(database.Text ?? ""))
            };
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedIndex < 0) return;
                database.Text = picker.SelectedIndex == 0 ? "" : choices[picker.SelectedIndex];
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10, Margin = new Thickness(0, 6, 0, 0) };
            row.Children.Add(new TextBlock { Text = $"Found {report.Databases.Count} — use", FontSize = 12, Foreground = Ink, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(picker, 1);
            row.Children.Add(picker);
            panel.Children.Add(row);
        }
        steps.IsVisible = false;
        var details = new Button { Name = "ConnectionTestDetails", Content = "Details", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0), Background = Brushes.Transparent, Foreground = Brush.Parse("#176FC1"), FontSize = 11.5 };
        details.Click += (_, _) =>
        {
            steps.IsVisible = !steps.IsVisible;
            details.Content = steps.IsVisible ? "Hide details" : "Details";
        };
        panel.Children.Add(details);
        panel.Children.Add(steps);
    }
}
