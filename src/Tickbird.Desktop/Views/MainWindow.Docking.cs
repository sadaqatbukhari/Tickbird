using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tickbird.Core.Settings;
using Newtonsoft.Json;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace Tickbird.Desktop.Views;

/// <summary>
/// Dockable Object Explorer, in the style of SSMS / Visual Studio tool windows: docked left or right,
/// auto-hidden to a tab on the window edge, floating in its own window, or hidden (F8 brings it back).
/// The pane itself (<c>ExplorerPane</c>) is a single control that is moved between hosts.
/// </summary>
public partial class MainWindow
{
    private const double SplitterWidth = 4;
    private const double DragThreshold = 6;
    private const double GuideSize = 40;
    private const double FloatTitleBarAllowance = 30;
    private static readonly IBrush DockAccent = Brush.Parse("#1a65a7");

    private Window? explorerWindow;
    private bool closingExplorerWindow;
    private bool floatPending;

    private Border? flyout;
    private Border? flyoutContent;
    private Thumb? flyoutThumb;
    private bool flyoutOpen;
    private bool flyoutEngaged;
    private readonly DispatcherTimer hoverOpenTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer hoverCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };

    private bool headerPressed;
    private bool headerDragging;
    private PixelPoint dragStartScreen;
    private PixelPoint dragStartWindowPosition;
    private Point dragGrabOffset;
    private Border? guideLeft;
    private Border? guideRight;
    private Border? guidePreview;

    /// <summary>The persisted Object Explorer layout (part of the settings file).</summary>
    public PaneLayout ExplorerLayout => app.Settings.Explorer ??= new PaneLayout();

    /// <summary>The floating Object Explorer window, when the pane is floating.</summary>
    public Window? ExplorerFloatWindow => explorerWindow;

    /// <summary>True while an auto-hidden Object Explorer is slid out.</summary>
    public bool IsExplorerFlyoutOpen => flyoutOpen;

    // ------------------------------------------------------------------ public commands

    public void DockExplorer(string side) => ChangeExplorerLayout(l => l.DockTo(side));

    public void FloatExplorer() => ChangeExplorerLayout(l => l.FloatOut());

    public void SetExplorerAutoHide(bool autoHide) => ChangeExplorerLayout(l => l.SetAutoHide(autoHide));

    public void HideExplorer() => ChangeExplorerLayout(l => l.Visible = false);

    /// <summary>View > Reset Window Layout: Object Explorer docked left, toolbar and status bar shown.</summary>
    public void ResetWindowLayout()
    {
        app.Settings.ShowToolbar = app.Settings.ShowStatusBar = true;
        ApplySettings();
        ChangeExplorerLayout(l => l.Reset());
    }

    /// <summary>Shows the Object Explorer wherever it lives (View > Object Explorer, F8) and focuses the tree.</summary>
    public void ShowExplorer()
    {
        if (!ExplorerLayout.Visible) ChangeExplorerLayout(l => l.Visible = true);
        if (ExplorerLayout.IsFloating) explorerWindow?.Activate();
        else if (ExplorerLayout.AutoHide) OpenFlyout(engaged: true);
        Dispatcher.UIThread.Post(() => Tree.Focus());
    }

    /// <summary>Ctrl+E: shows the Object Explorer wherever it lives and puts the caret in its filter box.</summary>
    public void FocusTreeFilter()
    {
        if (!ExplorerLayout.Visible) ChangeExplorerLayout(l => l.Visible = true);
        if (ExplorerLayout.IsFloating) explorerWindow?.Activate();
        else if (ExplorerLayout.AutoHide) OpenFlyout(engaged: true);
        Dispatcher.UIThread.Post(() =>
        {
            TreeFilter.Focus();
            TreeFilter.SelectAll();
        });
    }

    private void OnShowExplorer(object? s, RoutedEventArgs e) => ShowExplorer();
    private void OnResetWindowLayout(object? s, RoutedEventArgs e) => ResetWindowLayout();

    // ------------------------------------------------------------------ setup

    private void InitExplorerDocking()
    {
        // The pane may move to another window, so it carries the main view model itself.
        ExplorerPane.DataContext = DataContext;
        ExplorerLayout.Normalize();

        LeftAutoHideStrip.Child = CreateAutoHideTab();
        RightAutoHideStrip.Child = CreateAutoHideTab();
        BuildFlyout();
        BuildDockGuides();

        PaneMenuButton.ContextMenu = CreatePositionMenu();
        PaneMenuButton.Click += (_, _) =>
        {
            var menu = PaneMenuButton.ContextMenu!;
            FillPositionMenu(menu);
            menu.Open(PaneMenuButton);
        };
        PanePinButton.Click += (_, _) => SetExplorerAutoHide(!ExplorerLayout.AutoHide);
        PaneCloseButton.Click += (_, _) => HideExplorer();

        ExplorerHeader.ContextMenu = CreatePositionMenu();
        ExplorerHeader.PointerPressed += OnHeaderPointerPressed;
        ExplorerHeader.PointerMoved += OnHeaderPointerMoved;
        ExplorerHeader.PointerReleased += OnHeaderPointerReleased;
        ExplorerHeader.PointerCaptureLost += (_, _) => EndHeaderDrag();

        hoverOpenTimer.Tick += (_, _) => { hoverOpenTimer.Stop(); OpenFlyout(engaged: false); };
        hoverCloseTimer.Tick += (_, _) =>
        {
            hoverCloseTimer.Stop();
            if (flyoutEngaged || headerPressed || flyout?.IsPointerOver == true) return;
            if (Tree.ContextMenu?.IsOpen == true || ExplorerHeader.ContextMenu?.IsOpen == true) return;
            CloseFlyout();
        };

        AddHandler(PointerPressedEvent, OnWindowPointerPressedTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnWindowKeyDownTunnel, RoutingStrategies.Tunnel);
        Opened += (_, _) => { if (floatPending) ApplyExplorerLayout(); };
        Closing += (_, _) => OnMainWindowClosingForDocking();

        ApplyExplorerLayout();
    }

    private ContextMenu CreatePositionMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedRight };
        menu.Opening += (_, _) => FillPositionMenu(menu);
        return menu;
    }

    private void FillPositionMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        var l = ExplorerLayout;
        void Add(string header, bool isChecked, Action action)
        {
            var item = new MenuItem { Header = header, ToggleType = MenuItemToggleType.CheckBox, IsChecked = isChecked };
            // Let the menu close before the pane is moved.
            item.Click += (_, _) => Dispatcher.UIThread.Post(action);
            menu.Items.Add(item);
        }
        Add("_Float", l.IsFloating, FloatExplorer);
        Add("Dock _Left", l.Placement == PaneLayout.Left && !l.AutoHide, () => DockExplorer(PaneLayout.Left));
        Add("Dock _Right", l.Placement == PaneLayout.Right && !l.AutoHide, () => DockExplorer(PaneLayout.Right));
        Add("_Auto Hide", l.AutoHide && !l.IsFloating, () => SetExplorerAutoHide(true));
        menu.Items.Add(new Separator());
        Add("_Hide", false, HideExplorer);
    }

    // ------------------------------------------------------------------ layout

    private void ChangeExplorerLayout(Action<PaneLayout> change)
    {
        CaptureExplorerGeometry();
        change(ExplorerLayout);
        ExplorerLayout.Normalize();
        ApplyExplorerLayout();
        SaveExplorerLayout();
    }

    private void SaveExplorerLayout()
    {
        try { app.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Vm.Status = "Could not save the window layout: " + ex.Message; }
    }

    /// <summary>Puts the pane where <see cref="ExplorerLayout"/> says it belongs.</summary>
    private void ApplyExplorerLayout()
    {
        var layout = ExplorerLayout;
        CloseFlyout();
        DetachExplorerPane();
        SetDockColumn(left: true, width: null);
        SetDockColumn(left: false, width: null);
        LeftAutoHideStrip.IsVisible = RightAutoHideStrip.IsVisible = false;
        floatPending = false;

        if (layout.Visible && layout.IsFloating)
        {
            // A floating window needs a visible owner; until the main window opens, wait.
            if (IsVisible) ShowExplorerWindow();
            else floatPending = true;
        }
        else
        {
            CloseExplorerWindow();
            if (layout.Visible)
            {
                var left = !layout.IsDockedRight;
                ExplorerPane.BorderThickness = left ? new Thickness(0, 0, 1, 0) : new Thickness(1, 0, 0, 0);
                if (layout.AutoHide)
                {
                    (left ? LeftAutoHideStrip : RightAutoHideStrip).IsVisible = true;
                }
                else
                {
                    (left ? LeftDockHost : RightDockHost).Child = ExplorerPane;
                    SetDockColumn(left, layout.Width);
                }
            }
        }
        UpdateExplorerChrome();
    }

    private void SetDockColumn(bool left, double? width)
    {
        var columns = WorkArea.ColumnDefinitions;
        var dock = columns[left ? 1 : 5];
        var split = columns[left ? 2 : 4];
        var splitter = left ? LeftSplitter : RightSplitter;
        if (width is { } w)
        {
            dock.MinWidth = PaneLayout.MinPaneWidth;
            dock.Width = new GridLength(w);
            split.Width = new GridLength(SplitterWidth);
            splitter.IsVisible = true;
        }
        else
        {
            dock.MinWidth = 0;
            dock.Width = new GridLength(0);
            split.Width = new GridLength(0);
            splitter.IsVisible = false;
        }
    }

    private void DetachExplorerPane()
    {
        if (ReferenceEquals(LeftDockHost.Child, ExplorerPane)) LeftDockHost.Child = null;
        if (ReferenceEquals(RightDockHost.Child, ExplorerPane)) RightDockHost.Child = null;
        if (flyoutContent != null && ReferenceEquals(flyoutContent.Child, ExplorerPane)) flyoutContent.Child = null;
        if (explorerWindow != null && ReferenceEquals(explorerWindow.Content, ExplorerPane)) explorerWindow.Content = null;
    }

    /// <summary>Copies the current docked width / floating bounds into the layout before it changes or is saved.</summary>
    private void CaptureExplorerGeometry()
    {
        var layout = ExplorerLayout;
        if (!layout.Visible) return;
        if (layout.IsFloating)
        {
            if (explorerWindow == null) return;
            layout.FloatX = explorerWindow.Position.X;
            layout.FloatY = explorerWindow.Position.Y;
            if (explorerWindow.ClientSize.Width > 0) layout.FloatWidth = explorerWindow.ClientSize.Width;
            if (explorerWindow.ClientSize.Height > 0) layout.FloatHeight = explorerWindow.ClientSize.Height;
        }
        else if (!layout.AutoHide)
        {
            var width = WorkArea.ColumnDefinitions[layout.IsDockedRight ? 5 : 1].ActualWidth;
            if (width >= PaneLayout.MinPaneWidth) layout.Width = width;
        }
    }

    private void UpdateExplorerChrome()
    {
        var l = ExplorerLayout;
        PanePinButton.IsVisible = !l.IsFloating;
        PanePinIcon.RenderTransform = new RotateTransform(l.AutoHide ? 90 : 0);
        ToolTip.SetTip(PanePinButton, l.AutoHide ? "Dock (turn off Auto Hide)" : "Auto Hide");
    }

    private void OnMainWindowClosingForDocking()
    {
        var before = JsonConvert.SerializeObject(ExplorerLayout);
        CaptureExplorerGeometry();
        if (JsonConvert.SerializeObject(ExplorerLayout) != before) SaveExplorerLayout();
        CloseFlyout();
        CloseExplorerWindow();
    }

    // ------------------------------------------------------------------ floating window

    private void ShowExplorerWindow()
    {
        var layout = ExplorerLayout;
        ExplorerPane.BorderThickness = new Thickness(0);
        if (explorerWindow != null)
        {
            explorerWindow.Content = ExplorerPane;
            return;
        }
        var window = new Window
        {
            Title = "Object Explorer",
            Width = layout.FloatWidth,
            Height = layout.FloatHeight,
            MinWidth = 200,
            MinHeight = 200,
            ShowInTaskbar = false,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Icon = Icon,
            Background = Brushes.White,
            Foreground = Foreground,
            FontFamily = FontFamily,
            FontSize = FontSize,
            Content = ExplorerPane,
        };
        window.Position = FloatPosition(layout);
        window.Closing += (_, e) =>
        {
            if (closingExplorerWindow) return;
            // The window's own close button hides the pane; F8 brings it back floating.
            e.Cancel = true;
            Dispatcher.UIThread.Post(HideExplorer);
        };
        window.KeyDown += OnExplorerWindowKeyDown;
        explorerWindow = window;
        window.Show(this);
    }

    private PixelPoint FloatPosition(PaneLayout layout)
    {
        if (layout.FloatX is int x && layout.FloatY is int y)
        {
            var saved = new PixelPoint(x, y);
            // Only reuse the saved spot if it is still on a connected screen.
            if (Screens?.ScreenFromPoint(saved + new PixelPoint(40, 20)) != null) return saved;
        }
        var scale = DesktopScaling;
        return new PixelPoint(Position.X + (int)(60 * scale), Position.Y + (int)(140 * scale));
    }

    private void CloseExplorerWindow()
    {
        if (explorerWindow == null) return;
        var window = explorerWindow;
        explorerWindow = null;
        if (ReferenceEquals(window.Content, ExplorerPane)) window.Content = null;
        closingExplorerWindow = true;
        try { window.Close(); }
        finally { closingExplorerWindow = false; }
    }

    private async void OnExplorerWindowKeyDown(object? s, KeyEventArgs e)
    {
        // Menu hot keys only reach the main window; forward the tree ones.
        if (e.KeyModifiers != KeyModifiers.None) return;
        if (e.Key == Key.F5)
        {
            e.Handled = true;
            await Run(() => Do(RefreshNode));
        }
    }

    // ------------------------------------------------------------------ auto hide

    private Border CreateAutoHideTab()
    {
        var icon = new ShapePath
        {
            Width = 11, Height = 13, Stretch = Stretch.Uniform, Stroke = DockAccent, StrokeThickness = 1.4,
            VerticalAlignment = VerticalAlignment.Center,
            Data = Geometry.Parse("M4,5 C4,1 20,1 20,5 C20,9 4,9 4,5 M4,5 L4,19 C4,23 20,23 20,19 L20,5 M4,12 C4,16 20,16 20,12"),
        };
        var text = new TextBlock
        {
            Text = "Object Explorer", FontSize = 12.5, FontWeight = FontWeight.SemiBold,
            Foreground = DockAccent, VerticalAlignment = VerticalAlignment.Center,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { icon, text } };
        var tab = new Border
        {
            VerticalAlignment = VerticalAlignment.Top,
            Child = new LayoutTransformControl { LayoutTransform = new RotateTransform(90), Child = row },
        };
        tab.Classes.Add("autoHideTab");
        ToolTip.SetTip(tab, "Object Explorer");
        tab.PointerEntered += (_, _) =>
        {
            hoverCloseTimer.Stop();
            if (!flyoutOpen) hoverOpenTimer.Start();
        };
        tab.PointerExited += (_, _) =>
        {
            hoverOpenTimer.Stop();
            if (flyoutOpen && !flyoutEngaged) hoverCloseTimer.Start();
        };
        tab.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(tab).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            hoverOpenTimer.Stop();
            if (flyoutOpen && flyoutEngaged) CloseFlyout();
            else OpenFlyout(engaged: true);
        };
        return tab;
    }

    private void BuildFlyout()
    {
        var content = new Border();
        var thumb = new Thumb
        {
            Width = 5,
            Cursor = new Cursor(StandardCursorType.SizeWestEast),
            Template = new FuncControlTemplate<Thumb>((_, _) => new Border { Background = Brushes.Transparent }),
        };
        thumb.DragDelta += (_, e) =>
        {
            var layout = ExplorerLayout;
            var delta = layout.IsDockedRight ? -e.Vector.X : e.Vector.X;
            flyout!.Width = Math.Clamp(flyout.Width + delta, PaneLayout.MinPaneWidth, MaxFlyoutWidth());
            layout.Width = flyout.Width;
        };
        thumb.DragCompleted += (_, _) => SaveExplorerLayout();
        flyoutContent = content;
        flyoutThumb = thumb;
        var dock = new DockPanel { Children = { thumb, content } };
        flyout = new Border
        {
            Background = Brushes.White,
            BorderBrush = Brush.Parse("#c9d9e8"),
            BoxShadow = BoxShadows.Parse("0 0 18 0 #40000000"),
            Child = dock,
        };
        flyout.PointerEntered += (_, _) => hoverCloseTimer.Stop();
        flyout.PointerExited += (_, _) => { if (flyoutOpen && !flyoutEngaged) hoverCloseTimer.Start(); };
        FlyoutLayer.Children.Add(flyout);
    }

    private double MaxFlyoutWidth() =>
        Math.Max(PaneLayout.MinPaneWidth, CenterArea.Bounds.Width > 0 ? CenterArea.Bounds.Width - 40 : PaneLayout.MaxPaneWidth);

    private void OpenFlyout(bool engaged)
    {
        var layout = ExplorerLayout;
        if (!layout.Visible || !layout.AutoHide || layout.IsFloating || flyout == null) return;
        flyoutEngaged |= engaged;
        if (flyoutOpen) return;
        var right = layout.IsDockedRight;
        DetachExplorerPane();
        flyout.Width = Math.Min(layout.Width, MaxFlyoutWidth());
        flyout.HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        flyout.BorderThickness = right ? new Thickness(1, 0, 0, 0) : new Thickness(0, 0, 1, 0);
        DockPanel.SetDock(flyoutThumb!, right ? Dock.Left : Dock.Right);
        ExplorerPane.BorderThickness = new Thickness(0);
        flyoutContent!.Child = ExplorerPane;
        FlyoutLayer.IsVisible = true;
        flyoutOpen = true;
    }

    private void CloseFlyout()
    {
        hoverOpenTimer.Stop();
        hoverCloseTimer.Stop();
        if (!flyoutOpen) return;
        flyoutOpen = false;
        flyoutEngaged = false;
        if (flyoutContent != null && ReferenceEquals(flyoutContent.Child, ExplorerPane)) flyoutContent.Child = null;
        FlyoutLayer.IsVisible = false;
    }

    private void OnWindowPointerPressedTunnel(object? s, PointerPressedEventArgs e)
    {
        if (!flyoutOpen) return;
        if (e.Source is Visual v)
        {
            if (IsWithin(flyout, v)) { flyoutEngaged = true; hoverCloseTimer.Stop(); return; }
            // The edge tab toggles the flyout itself.
            if (IsWithin(LeftAutoHideStrip, v) || IsWithin(RightAutoHideStrip, v)) return;
        }
        CloseFlyout();
    }

    private static bool IsWithin(Visual? container, Visual target) =>
        container != null && (ReferenceEquals(container, target) || container.IsVisualAncestorOf(target));

    private void OnWindowKeyDownTunnel(object? s, KeyEventArgs e)
    {
        // Visual Studio style tab switching. Ctrl+Tab opens the switcher (most recently used first); Ctrl+PageUp/Down move
        // straight to the neighbouring tab.
        if (e.Key == Key.Tab && (e.KeyModifiers == KeyModifiers.Control || e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift)))
        {
            e.Handled = true;
            StepTabSwitcher(e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            return;
        }
        if (e.Key is Key.PageDown or Key.PageUp && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            var tabs = Vm.Tabs;
            if (tabs.Count > 1)
            {
                var index = Vm.SelectedTab == null ? 0 : tabs.IndexOf(Vm.SelectedTab);
                Vm.SelectedTab = tabs[(index + (e.Key == Key.PageUp ? -1 : 1) + tabs.Count) % tabs.Count];
            }
            return;
        }
        // SQL Server Management Studio shortcuts: F5 and Ctrl+E run the query. F5 still refreshes when the
        // Object Explorer has the focus, and Ctrl+E still jumps to the tree filter outside a query editor.
        if (CurrentQueryView != null && e.KeyModifiers == KeyModifiers.None && e.Key == Key.F5
            && !ExplorerPane.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            _ = Run(RunCurrent);
            return;
        }
        if (e.Key == Key.F5 && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            _ = Run(() => Do(RefreshNode));
            return;
        }
        if (CurrentQueryView is { } editor && e.Key == Key.E && e.KeyModifiers == KeyModifiers.Control
            && editor.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            _ = Run(RunCurrent);
            return;
        }
        if (e.Key == Key.E && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            FocusTreeFilter();
            return;
        }
        if (flyoutOpen && e.Key == Key.Escape && !TreeFilter.IsKeyboardFocusWithin) CloseFlyout();
    }

    // ------------------------------------------------------------------ drag to float / dock

    private void BuildDockGuides()
    {
        guidePreview = new Border
        {
            Background = Brush.Parse("#331a65a7"), BorderBrush = DockAccent, BorderThickness = new Thickness(2),
            IsVisible = false,
        };
        guideLeft = CreateDockGuide(left: true);
        guideRight = CreateDockGuide(left: false);
        DockGuideLayer.Children.Add(guidePreview);
        DockGuideLayer.Children.Add(guideLeft);
        DockGuideLayer.Children.Add(guideRight);
    }

    private static Border CreateDockGuide(bool left)
    {
        // A small window glyph with the docking side filled in.
        var glyph = new Grid { Width = 20, Height = 16 };
        glyph.Children.Add(new Border { BorderBrush = DockAccent, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(1) });
        glyph.Children.Add(new Border
        {
            Width = 7, Background = DockAccent,
            HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right,
        });
        return new Border
        {
            Width = GuideSize, Height = GuideSize, CornerRadius = new CornerRadius(5),
            Background = Brushes.White, BorderBrush = DockAccent, BorderThickness = new Thickness(1.5),
            BoxShadow = BoxShadows.Parse("0 2 8 0 #40000000"),
            Child = glyph,
        };
    }

    private void OnHeaderPointerPressed(object? s, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ExplorerHeader).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            EndHeaderDrag();
            Dispatcher.UIThread.Post(() => ChangeExplorerLayout(l => l.ToggleFloat()));
            return;
        }
        headerPressed = true;
        headerDragging = false;
        dragGrabOffset = e.GetPosition(ExplorerPane);
        dragStartScreen = ExplorerHeader.PointToScreen(e.GetPosition(ExplorerHeader));
        dragStartWindowPosition = explorerWindow?.Position ?? default;
        e.Pointer.Capture(ExplorerHeader);
    }

    private void OnHeaderPointerMoved(object? s, PointerEventArgs e)
    {
        if (!headerPressed) return;
        var screen = ExplorerHeader.PointToScreen(e.GetPosition(ExplorerHeader));
        var dx = screen.X - dragStartScreen.X;
        var dy = screen.Y - dragStartScreen.Y;
        if (!headerDragging)
        {
            var threshold = DragThreshold * DesktopScaling;
            if (Math.Abs(dx) < threshold && Math.Abs(dy) < threshold) return;
            headerDragging = true;
            hoverCloseTimer.Stop();
        }
        if (ExplorerLayout.IsFloating && explorerWindow != null)
        {
            // Floating: move the window with the pointer, and offer the dock targets.
            explorerWindow.Position = new PixelPoint(dragStartWindowPosition.X + dx, dragStartWindowPosition.Y + dy);
            UpdateDockGuides(screen, showFloatOutline: false);
        }
        else
        {
            // Docked: show where the pane would float, or the dock target under the pointer.
            UpdateDockGuides(screen, showFloatOutline: true);
        }
    }

    private void OnHeaderPointerReleased(object? s, PointerReleasedEventArgs e)
    {
        if (!headerPressed) return;
        var wasDragging = headerDragging;
        var screen = ExplorerHeader.PointToScreen(e.GetPosition(ExplorerHeader));
        var target = wasDragging ? UpdateDockGuides(screen, showFloatOutline: false) : null;
        var paneSize = ExplorerPane.Bounds.Size;
        EndHeaderDrag();
        e.Pointer.Capture(null);
        if (!wasDragging) return;

        if (target != null)
        {
            Dispatcher.UIThread.Post(() => DockExplorer(target));
        }
        else if (!ExplorerLayout.IsFloating)
        {
            var scale = DesktopScaling;
            var layout = ExplorerLayout;
            layout.FloatX = screen.X - (int)(dragGrabOffset.X * scale);
            layout.FloatY = screen.Y - (int)((dragGrabOffset.Y + FloatTitleBarAllowance) * scale);
            if (paneSize.Width > 0) layout.FloatWidth = paneSize.Width;
            if (paneSize.Height > 0) layout.FloatHeight = Math.Max(paneSize.Height, 360);
            Dispatcher.UIThread.Post(FloatExplorer);
        }
        else
        {
            CaptureExplorerGeometry();
            SaveExplorerLayout();
        }
    }

    private void EndHeaderDrag()
    {
        headerPressed = false;
        headerDragging = false;
        DockGuideLayer.IsVisible = false;
        if (guidePreview != null) guidePreview.IsVisible = false;
    }

    /// <summary>Shows the dock targets and returns the side under the pointer (Left/Right), or null to float.</summary>
    private string? UpdateDockGuides(PixelPoint screen, bool showFloatOutline)
    {
        var size = WorkArea.Bounds.Size;
        var p = WorkArea.PointToClient(screen);
        DockGuideLayer.IsVisible = true;

        var top = Math.Max(0, (size.Height - GuideSize) / 2);
        Canvas.SetLeft(guideLeft!, 12);
        Canvas.SetTop(guideLeft!, top);
        Canvas.SetLeft(guideRight!, Math.Max(0, size.Width - GuideSize - 12));
        Canvas.SetTop(guideRight!, top);

        string? target = null;
        if (HitsGuide(guideLeft!, p)) target = PaneLayout.Left;
        else if (HitsGuide(guideRight!, p)) target = PaneLayout.Right;

        var preview = guidePreview!;
        if (target != null)
        {
            var width = Math.Min(ExplorerLayout.Width, size.Width / 2);
            Canvas.SetLeft(preview, target == PaneLayout.Left ? 0 : size.Width - width);
            Canvas.SetTop(preview, 0);
            preview.Width = width;
            preview.Height = size.Height;
            preview.IsVisible = true;
        }
        else if (showFloatOutline)
        {
            var pane = ExplorerPane.Bounds.Size;
            Canvas.SetLeft(preview, p.X - dragGrabOffset.X);
            Canvas.SetTop(preview, p.Y - dragGrabOffset.Y);
            preview.Width = pane.Width;
            preview.Height = Math.Max(pane.Height, 360);
            preview.IsVisible = true;
        }
        else
        {
            preview.IsVisible = false;
        }
        return target;
    }

    private static bool HitsGuide(Control guide, Point p) =>
        new Rect(Canvas.GetLeft(guide), Canvas.GetTop(guide), GuideSize, GuideSize).Inflate(10).Contains(p);
}
