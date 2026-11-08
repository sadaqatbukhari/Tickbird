using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Tickbird.Desktop.Services;
using Tickbird.Desktop.ViewModels;

namespace Tickbird.Desktop.Views;

/// <summary>Visual Studio style Ctrl+Tab switcher: a popup listing the open tabs, most recently used first.</summary>
public partial class MainWindow
{
    private readonly List<TabViewModel> recentTabs = new();
    private readonly Popup switcherPopup = new() { Placement = PlacementMode.Center, IsLightDismissEnabled = false };
    private readonly ListBox switcherList = new() { Focusable = false, IsHitTestVisible = false, Background = Brushes.Transparent, BorderThickness = new Thickness(0), MinWidth = 300 };
    private readonly TextBlock switcherTitle = new() { FontWeight = FontWeight.Bold, FontSize = 15, Foreground = Brushes.Black };
    private readonly TextBlock switcherFooter = new() { FontSize = 12, Foreground = Brushes.Black };
    private List<TabViewModel> switcherTabs = new();
    private int switcherIndex;

    private bool SwitcherOpen => switcherPopup.IsOpen;

    private void InitTabSwitcher()
    {
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MainWindowViewModel.SelectedTab) || Vm.SelectedTab == null) return;
            recentTabs.Remove(Vm.SelectedTab);
            recentTabs.Insert(0, Vm.SelectedTab);
        };
        var header = new StackPanel { Spacing = 14 };
        var toolWindows = new StackPanel { Spacing = 6, Children = { Heading("Active Tool Windows"), new TextBlock { Text = "Object Explorer", Foreground = Brushes.Black } } };
        var files = new StackPanel { Spacing = 6, Children = { Heading("Active Files"), switcherList } };
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto"), ColumnSpacing = 36 };
        Grid.SetColumn(files, 1);
        columns.Children.Add(toolWindows);
        columns.Children.Add(files);
        header.Children.Add(switcherTitle);
        header.Children.Add(columns);
        header.Children.Add(switcherFooter);
        switcherList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<TabViewModel>((tab, _) =>
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new Image { Source = tab?.IconName == null ? null : ShellIcons.Get(tab.IconName), Width = 16, Height = 16 });
            row.Children.Add(new TextBlock { Text = tab?.Title, MaxWidth = 320, TextTrimming = TextTrimming.CharacterEllipsis });
            return row;
        });
        switcherPopup.Child = new Border
        {
            Background = Brushes.White, BorderBrush = Brush.Parse("#a8b8c8"), BorderThickness = new Thickness(1),
            Padding = new Thickness(18), Child = header,
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 16, Color = Color.FromArgb(70, 0, 0, 0), OffsetX = 0, OffsetY = 4 })
        };
        switcherPopup.PlacementTarget = WorkArea;
        WorkArea.Children.Add(switcherPopup);
        AddHandler(KeyUpEvent, OnSwitcherKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        Deactivated += (_, _) => CloseSwitcher(false);

        static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeight.Bold, Foreground = Brushes.Black, Margin = new Thickness(0, 0, 0, 6) };
    }

    private void StepTabSwitcher(bool backwards)
    {
        if (!SwitcherOpen)
        {
            var open = Vm.Tabs.ToList();
            if (open.Count < 2) return;
            // Most recently used first; tabs never selected yet follow in strip order.
            switcherTabs = recentTabs.Where(open.Contains).Concat(open.Where(t => !recentTabs.Contains(t))).ToList();
            switcherList.ItemsSource = switcherTabs;
            switcherIndex = 0;
            switcherPopup.IsOpen = true;
        }
        switcherIndex = (switcherIndex + (backwards ? -1 : 1) + switcherTabs.Count) % switcherTabs.Count;
        switcherList.SelectedIndex = switcherIndex;
        var tab = switcherTabs[switcherIndex];
        switcherTitle.Text = tab.Title;
        switcherFooter.Text = tab.Title;
    }

    private void CloseSwitcher(bool commit)
    {
        if (!SwitcherOpen) return;
        switcherPopup.IsOpen = false;
        if (commit && switcherIndex >= 0 && switcherIndex < switcherTabs.Count && Vm.Tabs.Contains(switcherTabs[switcherIndex]))
            Vm.SelectedTab = switcherTabs[switcherIndex];
    }

    private void OnSwitcherKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl) CloseSwitcher(true);
    }
}
