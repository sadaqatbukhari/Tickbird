using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Tickbird.Core.Data;
using Tickbird.Core.History;
using Tickbird.Core.Navigation;
using Tickbird.Core.Query;
using Tickbird.Core.Settings;
using Tickbird.Desktop.Services;
using Tickbird.Desktop.ViewModels;
using Tickbird.Desktop.Views.Requests;
using Newtonsoft.Json;

namespace Tickbird.Desktop.Views;

public partial class MainWindow : Window
{
    private sealed record ToolItem(string Icon, string Tip, Func<Task> Action, Func<CommandAvailability, bool> Enabled, string? Text = null, string? Label = null);

    private readonly TickbirdApp app;
    private readonly List<(Control Control, Func<CommandAvailability, bool> Enabled)> commandControls = new();
    private string? releaseUrl;

    private readonly bool showConnectionsOnStart;

    public MainWindow() : this(TickbirdApp.Current) { }

    public MainWindow(TickbirdApp app, bool showConnectionsOnStart = true)
    {
        this.app = app;
        this.showConnectionsOnStart = showConnectionsOnStart;
        InitializeComponent();
        DataContext = new MainWindowViewModel();
        Vm.PropertyChanged += (_, e) =>
        {
            // Stop depends on the selected tab's query, not only on the command set.
            if (e.PropertyName is nameof(MainWindowViewModel.Commands) or nameof(MainWindowViewModel.SelectedTab)) ApplyCommandState();
        };
        BuildToolBar();
        Closing += OnMainWindowClosingConfirm;
        InitTabSwitcher();
        BuildMenuState();
        BuildTreeContextMenu();
        ApplySettings();
        InitExplorerDocking();
        InitTreeFilter();
        Opened += async (_, _) => await OnOpenedAsync();
    }

    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext!;
    private TreeNodeViewModel? Node => Vm.SelectedNode;

    // ------------------------------------------------------------------ startup

    private async Task OnOpenedAsync()
    {
        await Run(async () =>
        {
            Vm.Status = "";
            Vm.UpdateCommands();
            _ = ShowUpdateNoticeAsync();
            if (!showConnectionsOnStart) return;
            await Task.Delay(250);
            if (IsVisible) await ShowConnectionsDialog();
            if (IsVisible && app.Settings.ShowGettingStarted) ShowGettingStarted();
        });
    }

    private GettingStartedWindow? gettingStarted;

    private void OnGettingStarted(object? s, RoutedEventArgs e) => ShowGettingStarted();

    /// <summary>Opens the Getting Started guide beside the main window (or brings it forward).</summary>
    public void ShowGettingStarted()
    {
        if (gettingStarted != null) { gettingStarted.Activate(); return; }
        var startedUtc = DateTime.UtcNow;
        bool OnDatabase() => Node is { Kind: NodeKind.Database or NodeKind.Measurement };
        var steps = new List<GuideStep>
        {
            new("1. Add a connection", "Enter the address of your InfluxDB server and save it.",
                () => app.Settings.Connections.Count > 0, "Add Connection…", () => Run(ShowConnectionsDialog)),
            new("2. Connect and pick a database", "In Manage Connections choose your server and click Connect. Then expand it in the Object Explorer and click a database.",
                () => CurrentQueryView != null || OnDatabase()),
            new("3. Open a query", "With a database selected, open a query tab. Its editor starts with a sample query you can change.",
                () => CurrentQueryView != null, "New Query", () => Run(() => Do(NewQuery)), OnDatabase),
            new("4. Run the query", "Press F5 (or Ctrl+E) in the editor. Select part of the text to run only that part. Results appear below.",
                () => app.History.Entries.Count > 0 && app.History.Entries[0].RunAtUtc >= startedUtc,
                "Run Query (F5)", () => Run(RunCurrent), () => CurrentQueryView != null),
        };
        var window = gettingStarted = new GettingStartedWindow(steps, app.Settings.ShowGettingStarted);
        window.Closed += (_, _) =>
        {
            gettingStarted = null;
            var show = window.ShowAtStartup.IsChecked == true;
            if (app.Settings.ShowGettingStarted == show) return;
            app.Settings.ShowGettingStarted = show;
            app.Save();
        };
        window.Opened += (_, _) =>
        {
            var scale = DesktopScaling;
            window.Position = new PixelPoint(Position.X + (int)((Bounds.Width - window.Width - 24) * scale), Position.Y + (int)(110 * scale));
        };
        window.Show(this);
    }

    private async Task ShowUpdateNoticeAsync()
    {
        var result = await UpdateService.CheckAsync();
        if (!result.UpdateAvailable) return;
        releaseUrl = result.ReleaseUrl;
        UpdateNotice.Text = result.Message + " View release";
        UpdateNotice.IsVisible = true;
        await Task.Delay(15000);
        UpdateNotice.IsVisible = false;
    }

    private void OnUpdateNoticeClicked(object? sender, PointerPressedEventArgs e)
    {
        if (releaseUrl != null) UpdateService.OpenUrl(releaseUrl);
    }

    private async Task Run(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (IsVisible) { await Dialogs.ShowExceptionAsync(this, ex); }
    }

    // ------------------------------------------------------------------ toolbar / menus

    private void BuildToolBar()
    {
        var items = new List<ToolItem?>
        {
            new("EditConnection", "Manage Connections", ShowConnectionsDialog, _ => true),
            new("Disconnect", "Disconnect", () => Do(DisconnectNode), c => c.Disconnect),
            new("RetentionPolicy", "Show Retention Policies", () => OpenRequestTab(n => new RetentionPolicyView(), "RetentionPolicy", n => n.Connection.Name + ".policies"), c => c.ShowPolicies),
            new("Users", "Show Users", () => OpenRequestTab(n => new UsersView(), "Users", n => n.Connection.Name + ".users"), c => c.ShowUsers),
            new("Stats", "Show Statistics", () => OpenRequestTab(n => new StatsView(), "Stats", n => n.Connection.Name + ".statistics"), c => c.ShowStats),
            new("Diagnostics", "Show Diagnostics", () => OpenRequestTab(n => new DiagnosticsView(), "Diagnostics", n => n.Connection.Name + ".diagnostics"), c => c.ShowDiagnostics),
            new("ShowQueries", "Show Queries", ShowQueries, c => c.ShowQueries),
            null,
            new("Refresh", "Refresh", () => Do(RefreshNode), c => c.Refresh),
            new("NewQuery", "New Query", () => Do(NewQuery), c => c.NewQuery, null, "New Query"),
            new("RunQuery", "Run Query", RunCurrent, c => c.RunQuery),
            new("StopQuery", "Stop Query", () => { CurrentQueryView?.CancelQuery(); return Task.CompletedTask; }, _ => CurrentQueryView?.IsRunning == true),
            new("Comment", "Comment Selected Lines", () => { CurrentQueryView?.CommentSelectedLines(); return Task.CompletedTask; }, c => c.CommentQuery),
            new("Uncomment", "Uncomment Selected Lines", () => { CurrentQueryView?.UncommentSelectedLines(); return Task.CompletedTask; }, c => c.CommentQuery),
            null,
            new("CreateDatabase", "Create Database", () => Do(CreateDatabase), c => c.CreateDatabase),
            new("ContinuousQuery", "Show Continuous Queries", () => Do(ShowContinuousQueries), c => c.ContinuousQueries),
            new("BackFill", "Run Backfill", () => Do(RunBackfill), c => c.Backfill),
            new("DropDatabase", "Drop Database", () => Do(DropDatabase), c => c.DropDatabase),
            null,
            new("TagKeys", "Show Tag Keys", () => OpenMeasurement<TagKeysView>("tag_keys", "TagKeys"), c => c.TagKeys),
            new("TagValues", "Show Tag Values", () => OpenMeasurement<TagValuesView>("tag_values", "TagValues"), c => c.TagValues),
            new("FieldKeys", "Show Field Keys", () => OpenMeasurement<FieldKeysView>("field_keys", "FieldKeys"), c => c.FieldKeys),
            new("Series", "Show Series", () => OpenMeasurement<SeriesView>("series", "Series"), c => c.ShowSeries),
            new("DropSeries", "Drop Series", () => Do(DropSeries), c => c.DropSeries),
            new("DropMeasurement", "Drop Measurement", () => Do(DropMeasurement), c => c.DropMeasurement),
        };

        foreach (var item in items)
        {
            if (item == null)
            {
                ToolBar.Children.Add(new Separator { Width = 1, Height = 18, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0), Background = Avalonia.Media.Brush.Parse("#d9e5f0") });
                continue;
            }
            var content = item.Text != null
                ? (Control)new TextBlock { Text = item.Text, FontWeight = Avalonia.Media.FontWeight.Bold, FontSize = 14, Foreground = Avalonia.Media.Brush.Parse("#1a65a7"), Width = 20, TextAlignment = Avalonia.Media.TextAlignment.Center }
                : new Image { Source = ShellIcons.Get(item.Icon), Width = 18, Height = 18 };
            var button = new Button();
            button.Classes.Add("shellTool");
            if (item.Label != null)
            {
                button.Width = double.NaN;
                button.Padding = new Thickness(6, 0);
                button.Content = new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4,
                    Children = { content, new TextBlock { Text = item.Label, VerticalAlignment = VerticalAlignment.Center, Foreground = Avalonia.Media.Brush.Parse("#1a1a1a") } }
                };
            }
            else button.Content = content;
            ToolTip.SetTip(button, item.Tip);
            var action = item.Action;
            button.Click += async (_, _) => await Run(action);
            ToolBar.Children.Add(button);
            commandControls.Add((button, item.Enabled));
        }
        ToolBar.OverflowChanged += (_, _) => ToolbarOverflowButton.IsVisible = ToolBar.HasOverflow;
    }

    /// <summary>"More commands" (⋯): the toolbar buttons that do not fit the current window width.</summary>
    private void OnToolbarOverflow(object? sender, RoutedEventArgs e)
    {
        var menu = ToolbarOverflowButton.ContextMenu ??= new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedRight };
        menu.Items.Clear();
        var children = ToolBar.Children;
        for (var i = ToolBar.VisibleCount; i < children.Count; i++)
        {
            if (children[i] is Separator)
            {
                if (menu.Items.Count > 0 && menu.Items[menu.Items.Count - 1] is not Separator) menu.Items.Add(new Separator());
                continue;
            }
            if (children[i] is not Button button || !button.IsVisible) continue;
            var item = new MenuItem { Header = ToolTip.GetTip(button) as string, IsEnabled = button.IsEnabled };
            item.Icon = button.Content switch
            {
                Image image => new Image { Source = image.Source, Width = 16, Height = 16 },
                TextBlock text => new TextBlock { Text = text.Text, FontWeight = Avalonia.Media.FontWeight.Bold, Foreground = text.Foreground, TextAlignment = Avalonia.Media.TextAlignment.Center },
                _ => null,
            };
            item.Click += (_, _) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.Open(ToolbarOverflowButton);
    }

    private async void OnToggleToolbar(object? s, RoutedEventArgs e)
    {
        app.Settings.ShowToolbar = !app.Settings.ShowToolbar;
        await Run(() => { app.Save(); ApplySettings(); return Task.CompletedTask; });
    }

    private async void OnToggleStatusBar(object? s, RoutedEventArgs e)
    {
        app.Settings.ShowStatusBar = !app.Settings.ShowStatusBar;
        await Run(() => { app.Save(); ApplySettings(); return Task.CompletedTask; });
    }

    private void BuildMenuState()
    {
        commandControls.Add((MenuRefresh, c => c.Refresh));
        commandControls.Add((MenuNewQuery, c => c.NewQuery));
        commandControls.Add((MenuOpenQuery, c => c.OpenSaveQuery));
        commandControls.Add((MenuSaveQuery, c => c.OpenSaveQuery));
        commandControls.Add((MenuRunQuery, c => c.RunQuery));
        commandControls.Add((MenuShowQueries, c => c.ShowQueries));
        commandControls.Add((MenuStopQuery, _ => CurrentQueryView?.IsRunning == true));
        commandControls.Add((MenuFind, c => c.CommentQuery));
        commandControls.Add((MenuReplace, c => c.CommentQuery));
        ApplyCommandState();
    }

    private void ApplyCommandState()
    {
        var commands = Vm.Commands;
        foreach (var (control, enabled) in commandControls) control.IsEnabled = enabled(commands);
    }

    private void BuildTreeContextMenu()
    {
        var menu = new ContextMenu();
        menu.Opening += (_, e) =>
        {
            menu.Items.Clear();
            var node = Node;
            if (node == null || node.Kind == NodeKind.Loading) { e.Cancel = true; return; }
            var c = Vm.Commands;
            void Add(string header, string icon, Func<Task> action, bool enabled = true)
            {
                var item = new MenuItem { Header = header, IsEnabled = enabled, Icon = new Image { Source = ShellIcons.Get(icon), Width = 20, Height = 20 } };
                item.Click += async (_, _) => await Run(action);
                menu.Items.Add(item);
            }
            switch (node.Kind)
            {
                case NodeKind.Group:
                    Add(node.IsExpanded ? "Collapse" : "Expand", "Group", () => { node.IsExpanded = !node.IsExpanded; return Task.CompletedTask; });
                    Add("Disconnect All", "Disconnect", () =>
                    {
                        foreach (var server in node.Children.ToList()) Disconnect(server);
                        return Task.CompletedTask;
                    });
                    break;
                case NodeKind.Connection:
                    Add("Refresh", "Refresh", () => Do(RefreshNode));
                    Add("Create Database", "CreateDatabase", () => Do(CreateDatabase));
                    Add("Show Retention Policies", "RetentionPolicy", () => OpenRequestTab(n => new RetentionPolicyView(), "RetentionPolicy", n => n.Connection.Name + ".policies"), c.ShowPolicies);
                    Add("Show Users", "Users", () => OpenRequestTab(n => new UsersView(), "Users", n => n.Connection.Name + ".users"), c.ShowUsers);
                    Add("Show Queries", "ShowQueries", ShowQueries, c.ShowQueries);
                    Add("Show Statistics", "Stats", () => OpenRequestTab(n => new StatsView(), "Stats", n => n.Connection.Name + ".statistics"), c.ShowStats);
                    Add("Diagnostics", "Diagnostics", () => OpenRequestTab(n => new DiagnosticsView(), "Diagnostics", n => n.Connection.Name + ".diagnostics"), c.ShowDiagnostics);
                    Add("Disconnect", "Disconnect", () => Do(DisconnectNode));
                    break;
                case NodeKind.Database:
                    Add("Refresh", "Refresh", () => Do(RefreshNode));
                    Add("New Query", "NewQuery", () => Do(NewQuery));
                    Add("Show Continuous Queries", "ContinuousQuery", () => Do(ShowContinuousQueries), c.ContinuousQueries);
                    Add("Run Backfill", "BackFill", () => Do(RunBackfill), c.Backfill);
                    Add("Drop Database", "DropDatabase", () => Do(DropDatabase), c.DropDatabase);
                    break;
                case NodeKind.Measurement:
                    Add("New Query", "NewQuery", () => Do(NewQuery));
                    Add("Show Tag Keys", "TagKeys", () => OpenMeasurement<TagKeysView>("tag_keys", "TagKeys"));
                    Add("Show Tag Values", "TagValues", () => OpenMeasurement<TagValuesView>("tag_values", "TagValues"));
                    Add("Show Field Keys", "FieldKeys", () => OpenMeasurement<FieldKeysView>("field_keys", "FieldKeys"));
                    Add("Show Series", "Series", () => OpenMeasurement<SeriesView>("series", "Series"), c.ShowSeries);
                    Add("Drop Measurement", "DropMeasurement", () => Do(DropMeasurement));
                    Add("Drop Series", "DropSeries", () => Do(DropSeries), c.DropSeries);
                    break;
            }
        };
        Tree.ContextMenu = menu;
    }

    private void ApplySettings()
    {
        var s = app.Settings;
        Time12.ToggleType = Time24.ToggleType = MenuItemToggleType.Radio;
        MenuUntrusted.ToggleType = MenuItemToggleType.CheckBox;
        MenuViewToolbar.ToggleType = MenuViewStatusBar.ToggleType = MenuItemToggleType.CheckBox;
        MenuViewToolbar.IsChecked = ToolbarHost.IsVisible = s.ShowToolbar;
        MenuViewStatusBar.IsChecked = StatusBar.IsVisible = s.ShowStatusBar;
        Time12.IsChecked = s.TimeFormat == TickbirdSettings.TimeFormat12Hour;
        Time24.IsChecked = !Time12.IsChecked;
        DateSystem.Header = "System default  " + DateTime.Now.ToString(
            TickbirdSettings.ResolveDateFormat(TickbirdSettings.DateFormatSystem), System.Globalization.CultureInfo.CurrentCulture) + "  (your regional settings)";
        foreach (var item in MenuDateFormat.Items.OfType<MenuItem>())
        {
            item.ToggleType = MenuItemToggleType.Radio;
            item.IsChecked = item.Tag as string == s.DateFormat;
        }
        MenuUntrusted.IsChecked = s.AllowUntrustedSsl;
        MenuKeepHistory.ToggleType = MenuItemToggleType.CheckBox;
        MenuKeepHistory.IsChecked = s.KeepQueryHistory;
        foreach (var item in MenuQueryTimeout.Items.OfType<MenuItem>())
        {
            item.ToggleType = MenuItemToggleType.Radio;
            item.IsChecked = item.Tag as string == s.QueryTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        foreach (var item in MenuHistoryExpiry.Items.OfType<MenuItem>())
        {
            item.ToggleType = MenuItemToggleType.Radio;
            item.IsChecked = item.Tag as string == s.QueryHistoryRetentionDays.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        app.History.SetRetention(s.QueryHistoryRetentionDays);
        SslIgnoreValidator.AllowUntrusted = s.AllowUntrustedSsl;
    }

    private async Task Do(Func<TreeNodeViewModel, Task> action)
    {
        if (Node is { HasConnection: true } node) await action(node);
    }

    // ------------------------------------------------------------------ menu handlers

    private async void OnManage(object? s, RoutedEventArgs e) => await Run(ShowConnectionsDialog);
    private async void OnRefresh(object? s, RoutedEventArgs e) => await Run(() => Do(RefreshNode));
    private async void OnNewQuery(object? s, RoutedEventArgs e) => await Run(() => Do(NewQuery));
    private async void OnShowQueries(object? s, RoutedEventArgs e) => await Run(ShowQueries);
    private async void OnRunQuery(object? s, RoutedEventArgs e) => await Run(RunCurrent);
    private async void OnOpenQuery(object? s, RoutedEventArgs e) { if (CurrentQueryView is { } q) await Run(q.OpenQueryAsync); }
    private async void OnSaveQuery(object? s, RoutedEventArgs e) { if (CurrentQueryView is { } q) await Run(q.SaveQueryAsync); }
    private void OnExit(object? s, RoutedEventArgs e) => Close();
    private void OnStopQuery(object? s, RoutedEventArgs e) => CurrentQueryView?.CancelQuery();
    private void OnFind(object? s, RoutedEventArgs e) => CurrentQueryView?.ShowFind(replace: false);
    private void OnReplace(object? s, RoutedEventArgs e) => CurrentQueryView?.ShowFind(replace: true);
    private void OnShowQueryHistory(object? s, RoutedEventArgs e) => ShowQueryHistory();
    private async void OnSaveSnippet(object? s, RoutedEventArgs e) { if (CurrentQueryView is { } q) await Run(q.SaveSnippetAsync); }
    private async void OnSnippets(object? s, RoutedEventArgs e) { if (CurrentQueryView is { } q) await Run(q.InsertSnippetAsync); }

    private async void OnQueryTimeout(object? s, RoutedEventArgs e)
    {
        if (!int.TryParse((s as Control)?.Tag as string, out var seconds)) return;
        app.Settings.QueryTimeoutSeconds = seconds;
        await Run(() => { app.Save(); ApplySettings(); return Task.CompletedTask; });
    }

    private async void OnHistoryExpiry(object? s, RoutedEventArgs e)
    {
        if (!int.TryParse((s as Control)?.Tag as string, out var days)) return;
        app.Settings.QueryHistoryRetentionDays = days;
        await Run(() => { app.Save(); ApplySettings(); return Task.CompletedTask; });
    }

    private async void OnKeepHistory(object? s, RoutedEventArgs e)
    {
        app.Settings.KeepQueryHistory = !app.Settings.KeepQueryHistory;
        await Run(() => { app.Save(); ApplySettings(); return Task.CompletedTask; });
    }

    private async void OnTreeDoubleTapped(object? s, TappedEventArgs e)
    {
        // Act on the item that was double-clicked (not on empty space below the tree).
        var item = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(true);
        if (item?.DataContext is not TreeNodeViewModel clicked) return;
        Vm.SelectedNode = clicked;

        // Double-clicking a measurement opens a new query for it (SELECT * FROM "measurement" ...).
        if (clicked.Kind == NodeKind.Measurement)
        {
            e.Handled = true;
            await Run(() => NewQuery(clicked));
            return;
        }
        // Double-clicking a connection or database toggles it open, like a standard tree.
        if (clicked is { Kind: NodeKind.Connection or NodeKind.Database, NeedsLoad: true })
            await Run(() => ExpandNodeChildren(clicked));
    }

    /// <summary>Double-click on a tree node (exposed for tests): a measurement opens a new query.</summary>
    public Task OpenNodeAsync(TreeNodeViewModel node)
    {
        Vm.SelectedNode = node;
        return node.Kind == NodeKind.Measurement ? NewQuery(node) : node.NeedsLoad ? ExpandNodeChildren(node) : Task.CompletedTask;
    }

    private void OnTreePointerPressed(object? s, PointerPressedEventArgs e)
    {
        // Right-click selects the node under the pointer so the context menu acts on it.
        if (!e.GetCurrentPoint(Tree).Properties.IsRightButtonPressed) return;
        var element = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(true);
        if (element?.DataContext is TreeNodeViewModel node) Vm.SelectedNode = node;
    }

    private async void OnTimeFormat(object? s, RoutedEventArgs e)
    {
        app.Settings.TimeFormat = (s as Control)?.Tag as string == "12" ? TickbirdSettings.TimeFormat12Hour : TickbirdSettings.TimeFormat24Hour;
        await Run(() => { app.Save(); ApplySettings(); return Task.CompletedTask; });
    }

    private async void OnDateFormat(object? s, RoutedEventArgs e)
    {
        var format = (s as Control)?.Tag as string;
        if (format == null || !TickbirdSettings.DateFormats.Any(f => f.Format == format)) return;
        app.Settings.DateFormat = format;
        await Run(() => { app.Save(); ApplySettings(); return Task.CompletedTask; });
    }

    private async void OnUntrustedSsl(object? s, RoutedEventArgs e)
    {
        app.Settings.AllowUntrustedSsl = MenuUntrusted.IsChecked;
        await Run(() => { app.Save(); ApplySettings(); return Task.CompletedTask; });
    }

    private async void OnAbout(object? s, RoutedEventArgs e) => await Run(async () =>
    {
        var update = await UpdateService.CheckAsync(refresh: true);
        await TickbirdDialogs.AboutAsync(this, UpdateService.InstalledVersionText, update.Message,
            update.UpdateAvailable ? update.ReleaseUrl : null, UpdateService.OpenUrl);
    });

    // ------------------------------------------------------------------ import / export

    private async void OnImportSettings(object? s, RoutedEventArgs e) => await Run(async () =>
    {
        var path = await FilePicker.OpenAsync(this, "Settings files", "*.json");
        if (path != null) await ImportFrom(path);
    });

    private async void OnImportLegacy(object? s, RoutedEventArgs e) => await Run(async () =>
    {
        var path = LegacySettingsImporter.DefaultWindowsPath();
        if (!File.Exists(path)) path = await FilePicker.OpenAsync(this, "InfluxDB Studio settings", "settings.json", "*.json") ?? "";
        if (path.Length > 0) await ImportFrom(path);
    });

    private async Task ImportFrom(string path)
    {
        var imported = LegacySettingsImporter.TryRead(path);
        if (imported == null)
        {
            await Dialogs.ShowErrorAsync(this, "The selected file is not a valid Tickbird settings file.", "Import Failed");
            return;
        }
        // Merge by connection ID so repeated imports never create duplicates; the previous file is kept as a backup.
        var added = app.Settings.MergeConnections(imported.Connections);
        app.Settings.MergeGroups(imported.Groups);
        app.Settings.TimeFormat = string.IsNullOrWhiteSpace(imported.TimeFormat) ? app.Settings.TimeFormat : imported.TimeFormat;
        app.Settings.DateFormat = string.IsNullOrWhiteSpace(imported.DateFormat) ? app.Settings.DateFormat : imported.DateFormat;
        app.Settings.AllowUntrustedSsl |= imported.AllowUntrustedSsl;
        app.Save();
        ApplySettings();
        Vm.Status = $"Imported {added} new connection(s) from {Path.GetFileName(path)}.";
        await Dialogs.ShowMessageAsync(this, "Import Settings", Vm.Status);
    }

    private async void OnExportSettings(object? s, RoutedEventArgs e) => await Run(async () =>
    {
        var path = await FilePicker.SaveAsync(this, "settings.json", "Settings files", "*.json");
        if (path == null) return;
        // Secrets are never exported.
        var export = new TickbirdSettings
        {
            TimeFormat = app.Settings.TimeFormat, DateFormat = app.Settings.DateFormat,
            AllowUntrustedSsl = app.Settings.AllowUntrustedSsl,
            Groups = app.Settings.AllGroups().ToList(),
            Connections = app.Settings.Connections.Select(c => new InfluxDbConnection(c.Id, c.Name, c.Host, c.Port, c.Username, null, c.UseSsl, c.Database, c.ServerVersion, null) { Group = c.Group }).ToList()
        };
        await File.WriteAllTextAsync(path, JsonConvert.SerializeObject(export, Formatting.Indented));
        Vm.Status = "Settings exported (passwords and tokens are not included).";
    });

    // ------------------------------------------------------------------ connections / tree

    private async Task ShowConnectionsDialog()
    {
        var dialog = new ManageConnectionsWindow(app);
        dialog.ConnectionUpdated += c =>
        {
            var node = Vm.FindConnectionNode(c.Id);
            if (node == null) return;
            node.Name = c.Name;
        };
        dialog.ConnectionRemoved += c =>
        {
            var node = Vm.FindConnectionNode(c.Id);
            if (node != null) Disconnect(node);
        };
        var chosen = await dialog.ShowAsync(this);
        Vm.RegroupConnections();
        if (chosen != null) await RenderConnection(chosen);
    }

    private async Task RenderConnection(InfluxDbConnection connection)
    {
        app.Connect(connection);
        var node = new TreeNodeViewModel(connection.Name, NodeKind.Connection, null, connection);
        node.Expanded += OnNodeExpanded;
        node.ResetToPlaceholder();
        Vm.AddConnectionNode(node);
        Vm.ReapplyTreeFilter();
        if (!string.IsNullOrWhiteSpace(connection.Database)) await RenderDatabases(node);
        Vm.SelectedNode ??= node;
        Vm.UpdateCommands();
    }

    private async void OnNodeExpanded(TreeNodeViewModel node)
    {
        if (!node.IsExpanded) return;
        await Run(() => ExpandNodeChildren(node));
    }

    private async Task ExpandNodeChildren(TreeNodeViewModel node)
    {
        if (!node.NeedsLoad) return;
        switch (node.Kind)
        {
            case NodeKind.Connection: await RenderDatabases(node); break;
            case NodeKind.Database: await RenderMeasurements(node); break;
        }
    }

    private TreeNodeViewModel CreateChild(TreeNodeViewModel parent, string name, NodeKind kind)
    {
        var child = new TreeNodeViewModel(name, kind, parent);
        child.Expanded += OnNodeExpanded;
        return child;
    }

    private async Task RenderDatabases(TreeNodeViewModel connectionNode)
    {
        var connection = connectionNode.Connection;
        var client = app.Connect(connection);
        var names = string.IsNullOrWhiteSpace(connection.Database)
            ? (await client.GetDatabaseNamesAsync()).ToList()
            : new List<string> { connection.Database };
        connectionNode.Children.Clear();
        foreach (var name in names)
        {
            var db = CreateChild(connectionNode, name, NodeKind.Database);
            db.ResetToPlaceholder();
            connectionNode.Children.Add(db);
        }
        // Load every database's measurements, fields and tags in the background for query IntelliSense.
        app.PrefetchSchemas(connection, names);
        Vm.ReapplyTreeFilter();
    }

    private async Task RenderMeasurements(TreeNodeViewModel dbNode)
    {
        var client = app.Connect(dbNode.Connection);
        var names = await client.GetMeasurementNamesAsync(dbNode.Name);
        dbNode.Children.Clear();
        foreach (var name in names) dbNode.Children.Add(CreateChild(dbNode, name, NodeKind.Measurement));
        Vm.ReapplyTreeFilter();
    }

    private async Task RefreshNode(TreeNodeViewModel node)
    {
        switch (node.Kind)
        {
            case NodeKind.Connection:
                app.Schemas.Invalidate(node.Connection.Id);
                node.ResetToPlaceholder();
                await RenderDatabases(node);
                break;
            case NodeKind.Database:
                app.Schemas.Invalidate(node.Connection.Id, node.Name);
                app.PrefetchSchemas(node.Connection, new[] { node.Name });
                node.ResetToPlaceholder();
                await RenderMeasurements(node);
                break;
        }
        Vm.UpdateCommands();
    }

    private Task DisconnectNode(TreeNodeViewModel node)
    {
        Disconnect(node);
        return Task.CompletedTask;
    }

    private void Disconnect(TreeNodeViewModel node)
    {
        var root = node;
        while (root.Parent != null) root = root.Parent;
        var connection = root.Connection;
        Vm.CloseTabsForConnection(connection.Id);
        app.Disconnect(connection);
        Vm.RemoveConnectionNode(root);
        if (Vm.SelectedNode == null || !Vm.IsInTree(Vm.SelectedNode))
            Vm.SelectedNode = Vm.Nodes.FirstOrDefault();
        Vm.UpdateCommands();
    }

    private static TreeNodeViewModel RootOf(TreeNodeViewModel node)
    {
        while (node.Parent != null) node = node.Parent;
        return node;
    }

    // ------------------------------------------------------------------ database / measurement commands

    private async Task CreateDatabase(TreeNodeViewModel node)
    {
        var connection = node.Connection;
        var client = app.Connect(connection);
        var existing = (await client.GetDatabaseNamesAsync()).ToList();
        var name = await TickbirdDialogs.CreateDatabaseAsync(this, connection.Name, connection.Database, existing);
        if (name == null) return;
        var response = await client.CreateDatabaseAsync(name);
        if (!response.Success) { await Dialogs.ShowErrorAsync(this, response.Body, "Error Creating Database"); return; }
        var connectionNode = RootOf(node);
        var db = CreateChild(connectionNode, name, NodeKind.Database);
        db.ResetToPlaceholder();
        connectionNode.Children.Add(db);
        connectionNode.IsExpanded = true;
        Vm.SelectedNode = db;
    }

    private async Task DropDatabase(TreeNodeViewModel node)
    {
        if (node.Kind != NodeKind.Database) return;
        if (!await Dialogs.ConfirmByNameAsync(this, "Drop Database",
                $"Drop database \"{node.Name}\" on {node.Connection.Name}? All of its measurements and data will be permanently deleted.",
                node.Name)) return;
        var response = await app.Connect(node.Connection).DropDatabaseAsync(node.Name);
        if (!response.Success) { await Dialogs.ShowErrorAsync(this, response.Body, "Error Dropping Database"); return; }
        app.Schemas.Invalidate(node.Connection.Id, node.Name);
        node.Parent!.Children.Remove(node);
        Vm.SelectedNode = node.Parent;
    }

    private async Task DropMeasurement(TreeNodeViewModel node)
    {
        if (node.Kind != NodeKind.Measurement) return;
        if (!await Dialogs.ConfirmByNameAsync(this, "Drop Measurement",
                $"Drop measurement \"{node.Name}\" from database \"{node.Parent!.Name}\" on {node.Connection.Name}? All of its data will be permanently deleted.",
                node.Name)) return;
        var response = await app.Connect(node.Connection).DropMeasurementAsync(node.Parent!.Name, node.Name);
        if (!response.Success) { await Dialogs.ShowErrorAsync(this, response.Body, "Error Dropping Measurement"); return; }
        app.Schemas.Invalidate(node.Connection.Id, node.Parent!.Name);
        var parent = node.Parent;
        parent.Children.Remove(node);
        Vm.SelectedNode = parent;
    }

    private async Task DropSeries(TreeNodeViewModel node)
    {
        if (node.Kind != NodeKind.Measurement) return;
        if (!await Dialogs.ConfirmByNameAsync(this, "Drop Series",
                $"Drop all series of measurement \"{node.Name}\" in database \"{node.Parent!.Name}\" on {node.Connection.Name}? Their data will be permanently deleted.",
                node.Name)) return;
        var response = await app.Connect(node.Connection).DropSeriesAsync(node.Parent!.Name, node.Name);
        if (!response.Success) await Dialogs.ShowErrorAsync(this, response.Body, "Error Dropping Series");
    }

    private async Task RunBackfill(TreeNodeViewModel node)
    {
        if (node.Kind != NodeKind.Database) return;
        var client = app.Connect(node.Connection);
        var backfill = await TickbirdDialogs.BackfillAsync(this, client, node.Name);
        if (backfill == null) return;
        var response = await client.BackfillAsync(node.Name, backfill);
        if (!response.Success) await Dialogs.ShowErrorAsync(this, response.Body);
    }

    // ------------------------------------------------------------------ tabs

    private QueryView? CurrentQueryView => Vm.SelectedTab?.Content as QueryView;

    private async Task OpenView(TabViewModel tab, RequestView view, bool execute = true)
    {
        Vm.AddTab(tab);
        if (!execute) return;
        // The view must be attached before it can show dialogs on failure.
        await Task.Yield();
        await view.ExecuteAsync();
    }

    /// <summary>Server-level Show views (policies, users, statistics, diagnostics): one tab per server; reopening activates it.</summary>
    private async Task OpenRequestTab(Func<TreeNodeViewModel, RequestView> create, string icon, Func<TreeNodeViewModel, string> title)
    {
        if (Node is not { HasConnection: true } node) return;
        var id = TabIdentity.Show(icon, node.Connection.Id);
        if (Vm.FocusExisting(id)) return;
        var view = create(node);
        view.Client = app.Connect(node.Connection);
        await OpenView(new TabViewModel(id, title(node), TabKind.Request, node.Connection.Id, icon, view), view);
    }

    /// <summary>Measurement-level Show views (tag keys/values, field keys, series): one tab per measurement; reopening activates it.</summary>
    private async Task OpenMeasurement<T>(string suffix, string icon) where T : MeasurementView, new()
    {
        if (Node is not { Kind: NodeKind.Measurement } node) return;
        var database = node.Parent!.Name;
        var id = TabIdentity.Show(suffix, node.Connection.Id, database, node.Name);
        if (Vm.FocusExisting(id)) return;
        var view = new T { Client = app.Connect(node.Connection), Database = database, Measurement = node.Name };
        var title = node.Connection.Name + "." + node.Name + "." + suffix;
        await OpenView(new TabViewModel(id, title, TabKind.Request, node.Connection.Id, icon, view), view);
    }

    private async Task ShowContinuousQueries(TreeNodeViewModel node)
    {
        if (node.Kind != NodeKind.Database) return;
        // One Continuous Queries tab per database; reopening activates it.
        var id = TabIdentity.Show("continuous_queries", node.Connection.Id, node.Name);
        if (Vm.FocusExisting(id)) return;
        var client = app.Connect(node.Connection);
        var view = new ContinuousQueryView { Client = client, Database = node.Name };
        view.RunBackfill = async () =>
        {
            var backfill = await TickbirdDialogs.BackfillAsync(this, client, node.Name);
            if (backfill == null) return;
            var response = await client.BackfillAsync(node.Name, backfill);
            if (!response.Success) await Dialogs.ShowErrorAsync(this, response.Body);
        };
        var title = node.Connection.Name + "." + node.Name + " CQs";
        await OpenView(new TabViewModel(id, title, TabKind.Request, node.Connection.Id, "ContinuousQuery", view), view);
    }

    private Task NewQuery(TreeNodeViewModel node)
    {
        if (node.Kind is not (NodeKind.Database or NodeKind.Measurement)) return Task.CompletedTask;
        var connection = node.Connection;
        var measurement = node.Kind == NodeKind.Measurement ? node.Name : "measurement";
        OpenQueryTab(connection, node.DatabaseName, QueryText.SampleQuery(connection.ServerVersion, measurement),
            node.Kind == NodeKind.Measurement ? node.Name : null);
        return Task.CompletedTask;
    }

    /// <summary>Opens a new, independent query tab with the given text.</summary>
    private QueryView OpenQueryTab(InfluxDbConnection connection, string? database, string text, string? measurement = null)
    {
        var view = new QueryView { Client = app.Connect(connection), Database = database };
        view.RunningChanged += (_, _) => ApplyCommandState();
        if (!string.IsNullOrWhiteSpace(database))
        {
            view.SchemaLoader = () => app.GetSchemaAsync(connection, database);
            view.TagValueLoader = (measurementName, tag) => app.GetTagValuesAsync(connection, database, measurementName, tag);
        }
        view.EditorText = text;
        // Every New Query is an independent tab with its own unique ID.
        var title = connection.Name + "." + database + (measurement != null ? "." + measurement : "");
        Vm.AddTab(new TabViewModel(MainWindowViewModel.NewQueryTabId(), title, TabKind.Query, connection.Id, "RunQuery", view));
        _ = view.InitializeIntellisenseAsync();
        return view;
    }

    // ------------------------------------------------------------------ query history

    private const string QueryHistoryTabId = "query-history";

    /// <summary>View › Query History (Ctrl+Shift+H): one tab listing every query run from an editor.</summary>
    public void ShowQueryHistory()
    {
        app.History.Prune();
        if (Vm.FocusExisting(QueryHistoryTabId)) return;
        var view = new QueryHistoryView(app.History, OpenFromHistoryAsync, RunFromHistoryAsync);
        Vm.AddTab(new TabViewModel(QueryHistoryTabId, "Query History", TabKind.Tool, null, "History", view));
    }

    /// <summary>
    /// Reopens a history entry in a new query tab on its connection and database. When that server is not
    /// connected yet, it is connected first (and shown in the Object Explorer), so the query can be run at once.
    /// </summary>
    public Task OpenFromHistoryAsync(QueryHistoryEntry entry) => OpenFromHistoryAsync(entry, run: false);

    /// <summary>Opens a history entry in a new query tab and runs it at once.</summary>
    public Task RunFromHistoryAsync(QueryHistoryEntry entry) => OpenFromHistoryAsync(entry, run: true);

    private async Task OpenFromHistoryAsync(QueryHistoryEntry entry, bool run)
    {
        var connection = app.Settings.Connections.FirstOrDefault(c => c.Id == entry.ConnectionId);
        if (connection == null)
        {
            await Dialogs.ShowMessageAsync(this, "Query History",
                $"The connection \"{entry.ConnectionName}\" no longer exists, so the query cannot be opened on it. Use Copy to paste it into another query tab.");
            return;
        }
        // Called from the history tab's buttons: report problems here instead of letting them escape.
        await Run(async () =>
        {
            if (Vm.FindConnectionNode(connection.Id) == null && !await ConnectForHistoryAsync(connection, entry.Database)) return;
            var tab = OpenQueryTab(connection, entry.Database, entry.Query);
            if (run) await tab.ExecuteAsync();
        });
    }

    /// <summary>Checks the server answers, adds it to the Object Explorer and selects the query's database.</summary>
    private async Task<bool> ConnectForHistoryAsync(InfluxDbConnection connection, string? database)
    {
        Vm.Status = $"Connecting to {connection.Name}…";
        string? problem;
        try
        {
            var ping = await app.Connect(connection).PingAsync().WaitAsync(TimeSpan.FromSeconds(15));
            problem = ping is { Success: true } ? null : "The server did not answer the ping.";
        }
        catch (TimeoutException)
        {
            problem = "The server did not answer within 15 seconds.";
        }
        catch (Exception ex)
        {
            problem = ex.Message;
        }
        if (problem != null)
        {
            app.Disconnect(connection);
            Vm.Status = $"Could not connect to {connection.Name}.";
            await Dialogs.ShowErrorAsync(this, $"Could not connect to {connection.Name} ({connection.HttpConnectionString}).\n\n{problem}", "Query History");
            return false;
        }

        try
        {
            await RenderConnection(connection);
        }
        catch (Exception ex)
        {
            // The server answered, so the query tab can still be used; only the tree could not be filled.
            Vm.Status = $"Connected to {connection.Name}, but its databases could not be listed: {ex.Message}";
            return true;
        }
        Vm.Status = $"Connected to {connection.Name}.";
        // Show the query's database in the Object Explorer; if the list cannot be read, the query tab still works.
        var node = Vm.FindConnectionNode(connection.Id);
        if (node == null) return true;
        try
        {
            if (node.NeedsLoad) await ExpandNodeChildren(node);
            node.IsExpanded = true;
            var db = node.Children.FirstOrDefault(c => c.Kind == NodeKind.Database && string.Equals(c.Name, database, StringComparison.Ordinal));
            Vm.SelectedNode = db ?? node;
        }
        catch (Exception ex)
        {
            Vm.Status = $"Connected to {connection.Name}, but its databases could not be listed: {ex.Message}";
        }
        return true;
    }

    private async Task ShowQueries()
    {
        if (Node is not { Kind: NodeKind.Connection } node) return;
        var connection = node.Connection;
        // One Show Queries tab per connection, keyed by connection ID (display names may repeat).
        if (Vm.FocusExisting(MainWindowViewModel.ShowQueriesTabId(connection))) return;
        var view = new RunningQueriesView { Client = app.Connect(connection) };
        await OpenView(new TabViewModel(MainWindowViewModel.ShowQueriesTabId(connection), connection.Name + ".queries",
            TabKind.ShowQueries, connection.Id, "ShowQueries", view), view);
    }

    private async Task RunCurrent()
    {
        if (Vm.SelectedTab?.Content is RequestView view) await view.ExecuteAsync();
    }

    /// <summary>Asks about each tab with unsaved query edits. Returns false when the user cancels.</summary>
    private async Task<bool> ConfirmDiscardAsync(IEnumerable<TabViewModel> tabs)
    {
        foreach (var tab in tabs.ToList())
        {
            if (tab.Content is not QueryView { IsDirty: true } query) continue;
            Vm.SelectedTab = tab;
            var choice = await Dialogs.AskSaveChangesAsync(this, $"The query in \"{tab.Title}\" has unsaved changes. Do you want to save it?");
            if (choice == Dialogs.SaveChoice.Cancel) return false;
            if (choice == Dialogs.SaveChoice.Save && !await query.SaveQueryAsync()) return false;
        }
        return true;
    }

    private async void OnCloseTab(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not TabViewModel tab) return;
        if (await ConfirmDiscardAsync(new[] { tab })) Vm.CloseTab(tab);
    }

    private async void OnCloseAllButThis(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not TabViewModel keep) return;
        if (await ConfirmDiscardAsync(Vm.Tabs.Where(t => t != keep))) Vm.CloseAllBut(keep);
    }

    private async void OnCloseAll(object? sender, RoutedEventArgs e)
    {
        if (await ConfirmDiscardAsync(Vm.Tabs)) Vm.CloseAll();
    }

    private bool closeConfirmed;

    private async void OnMainWindowClosingConfirm(object? sender, WindowClosingEventArgs e)
    {
        if (closeConfirmed || !Vm.Tabs.Any(t => t.Content is QueryView { IsDirty: true })) return;
        e.Cancel = true;
        if (await ConfirmDiscardAsync(Vm.Tabs)) { closeConfirmed = true; Close(); }
    }
}
