using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Tickbird.Desktop.Services;

namespace Tickbird.Desktop.Tests;

public class GettingStartedTests
{
    [AvaloniaFact]
    public void Guide_TicksOffSteps_AndEnablesTheCurrentStepButton()
    {
        var added = false;
        var opened = false;
        var steps = new List<GuideStep>
        {
            new("1. Add", "hint", () => added, "Add", () => { added = true; return Task.CompletedTask; }),
            new("2. Open", "hint", () => opened, "Open", () => { opened = true; return Task.CompletedTask; }, () => added),
        };
        var window = new GettingStartedWindow(steps, showAtStartup: true);
        window.Show();
        window.Refresh();

        var buttons = Find<Button>(window).Where(b => (b.Content as string) is "Add" or "Open").ToList();
        Assert.True(buttons[0].IsEnabled);
        Assert.False(buttons[1].IsEnabled);

        added = true;
        window.Refresh();
        Assert.False(buttons[0].IsEnabled);
        Assert.True(buttons[1].IsEnabled);
        Assert.True(window.ShowAtStartup.IsChecked);
        window.Close();
    }

    private static IEnumerable<T> Find<T>(Avalonia.Visual root) where T : class =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<T>();
}
