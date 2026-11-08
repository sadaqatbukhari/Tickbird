using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Tickbird.Core.Data;
using Tickbird.Core.Providers;
using Tickbird.Core.Navigation;
using Tickbird.Desktop.Services;

namespace Tickbird.Desktop.Views.Requests;

public sealed class RunningQueriesView : ListRequestView
{
    private readonly Button kill = ToolButton("Kill Query", "KillQuery", "Kill the selected query");

    private const int QueryColumn = 4; // after the row number column
    private readonly TextBox fullQuery = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Height = 150, FontSize = 12,
        FontFamily = new Avalonia.Media.FontFamily("Cascadia Mono,Consolas,Menlo,monospace"),
        Margin = new Avalonia.Thickness(0, 8, 0, 0), Watermark = "Select a query to see all of it here."
    };

    public RunningQueriesView()
    {
        kill.IsEnabled = false;
        kill.Click += async (_, _) => await Guard(KillAsync);
        Header.Children.Add(kill);
        // The grid cuts long queries short, so the selected one is shown in full under it.
        Avalonia.Controls.DockPanel.SetDock(fullQuery, Avalonia.Controls.Dock.Bottom);
        Layout.Children.Insert(Layout.Children.IndexOf(Grid), fullQuery);
        Grid.SelectionChanged += (_, _) =>
        {
            kill.IsEnabled = Grid.SelectedItem != null;
            fullQuery.Text = (Grid.SelectedItem as GridRow)?[QueryColumn] ?? "";
        };
    }

    public override string ExportFileNameStem => "queries";

    protected override async Task<TableData> LoadAsync()
    {
        // Longest running first; a duration that cannot be read goes last. Equal durations keep the lowest PID first.
        var queries = (await Client.GetRunningQueriesAsync() ?? Enumerable.Empty<RunningQuery>())
            .OrderByDescending(q => Tickbird.Core.Query.DurationText.ToMilliseconds(q.Duration) ?? -1)
            .ThenBy(q => q.PID)
            .ToList();
        return new TableData(new[] { "PID", "Duration", "Database", "Query" },
            queries.Select(q => (IReadOnlyList<string?>)new[] { q.PID.ToString(), q.Duration, q.Database, q.Query }).ToList());
    }

    private async Task KillAsync()
    {
        var pid = (Grid.SelectedItem as GridRow)?[1];
        if (!int.TryParse(pid, out var id) || OwnerWindow == null) return;
        if (!await Dialogs.ConfirmAsync(OwnerWindow, "Confirm Kill", $"Kill query {id}?")) return;
        var response = await Client.KillQueryAsync(id);
        if (!response.Success) await ShowError(response.Body);
        await ExecuteAsync();
    }
}

public sealed class RetentionPolicyView : RequestView
{
    private readonly ComboBox databases = new() { MinWidth = 200 };
    private readonly DataGrid grid = GridFactory.Create();
    private readonly Button create = ToolButton("Create", "CreateRetentionPolicy", "Create a retention policy");
    private readonly Button alter = ToolButton("Alter", "EditRetentionPolicy", "Alter the selected policy");
    private readonly Button drop = ToolButton("Drop", "DropRetentionPolicy", "Drop the selected policy");
    private List<RetentionPolicy> policies = new();
    private bool binding;

    public RetentionPolicyView()
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(6) };
        header.Children.Add(new TextBlock { Text = "Database:", VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(databases);
        header.Children.Add(create); header.Children.Add(alter); header.Children.Add(drop);
        GridFactory.SetColumns(grid, new[] { "Name", "Duration", "Shard Group Duration", "Replication", "Default" });
        grid.SelectionMode = DataGridSelectionMode.Single;
        var panel = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        panel.Children.Add(grid);
        Content = TickbirdTheme.Card(panel);

        databases.SelectionChanged += async (_, _) => { if (!binding) await Guard(BindPoliciesAsync); };
        grid.SelectionChanged += (_, _) => UpdateButtons();
        create.Click += async (_, _) => await Guard(CreateAsync);
        alter.Click += async (_, _) => await Guard(AlterAsync);
        drop.Click += async (_, _) => await Guard(DropAsync);
        UpdateButtons();
    }

    private string? SelectedDatabase => databases.SelectedItem as string;
    private RetentionPolicy? Selected => (grid.SelectedItem as GridRow)?.Tag as RetentionPolicy;

    private void UpdateButtons()
    {
        create.IsEnabled = SelectedDatabase != null;
        alter.IsEnabled = drop.IsEnabled = Selected != null;
    }

    /// <summary>Columns follow the database: InfluxDB policies have replication and a default; TimescaleDB ones belong to a table.</summary>
    private void ConfigureColumns()
    {
        if (Client.Connection.Profile.RetentionPerTable) GridFactory.SetColumns(grid, new[] { "Table", "Keep data for", "Chunk interval" });
        else GridFactory.SetColumns(grid, new[] { "Name", "Duration", "Shard Group Duration", "Replication", "Default" });
    }

    public override async Task ExecuteAsync()
    {
        ConfigureColumns();
        var keep = SelectedDatabase;
        var names = !string.IsNullOrWhiteSpace(Client.Connection.Database)
            ? new[] { Client.Connection.Database }.AsEnumerable()
            : await Client.GetDatabaseNamesAsync();
        binding = true;
        databases.ItemsSource = names.ToList();
        databases.SelectedItem = keep != null && names.Contains(keep) ? keep : names.FirstOrDefault();
        binding = false;
        await BindPoliciesAsync();
    }

    private async Task BindPoliciesAsync()
    {
        var keep = Selected?.Name;
        if (SelectedDatabase == null) { grid.ItemsSource = null; UpdateButtons(); return; }
        policies = (await Client.GetRetentionPoliciesAsync(SelectedDatabase)).ToList();
        var perTable = Client.Connection.Profile.RetentionPerTable;
        var rows = policies.Select(p => new GridRow(perTable
            ? new[] { p.Name, p.Duration, p.ShardGroupDuration }
            : new[] { p.Name, p.Duration, p.ShardGroupDuration, p.ReplicationCopies.ToString(), p.Default ? GridFactory.CheckMark : null }, p)).ToList();
        grid.ItemsSource = rows;
        grid.SelectedItem = rows.FirstOrDefault(r => ((RetentionPolicy)r.Tag!).Name == keep);
        UpdateButtons();
    }

    private async Task CreateAsync()
    {
        if (OwnerWindow == null || SelectedDatabase == null) return;
        var input = await TickbirdDialogs.RetentionPolicyAsync(OwnerWindow, null, Client.Connection.Profile);
        if (input == null) return;
        var response = await Client.CreateRetentionPolicyAsync(SelectedDatabase, input.Name, input.Duration, input.Replication, input.IsDefault);
        if (!response.Success) await ShowError(response.Body);
        await BindPoliciesAsync();
    }

    private async Task AlterAsync()
    {
        var policy = Selected;
        if (OwnerWindow == null || SelectedDatabase == null || policy == null) return;
        var input = await TickbirdDialogs.RetentionPolicyAsync(OwnerWindow, policy, Client.Connection.Profile);
        if (input == null) return;
        var response = await Client.AlterRetentionPolicyAsync(SelectedDatabase, input.Name, input.Duration, input.Replication, input.IsDefault);
        if (!response.Success) { await ShowError(response.Body); }
        else if (policy.Default && !input.IsDefault) await PromoteFallbackDefaultAsync(policy.Name);
        await BindPoliciesAsync();
    }

    private async Task DropAsync()
    {
        var policy = Selected;
        if (OwnerWindow == null || SelectedDatabase == null || policy == null) return;
        if (!await Dialogs.ConfirmAsync(OwnerWindow, "Confirm Drop", $"Drop retention policy: {policy.Name}?")) return;
        var response = await Client.DropRetentionPolicyAsync(SelectedDatabase, policy.Name);
        if (!response.Success) await ShowError(response.Body);
        if (policy.Default) await PromoteFallbackDefaultAsync(policy.Name);
        await BindPoliciesAsync();
    }

    /// <summary>InfluxDB needs a default policy; prefer "autogen", otherwise any other policy.</summary>
    private async Task PromoteFallbackDefaultAsync(string excluded)
    {
        var all = (await Client.GetRetentionPoliciesAsync(SelectedDatabase)).ToList();
        var fallback = RetentionPolicyRules.ChooseFallbackDefault(all, excluded);
        if (fallback != null)
            await Client.AlterRetentionPolicyAsync(SelectedDatabase, fallback.Name, fallback.Duration, fallback.ReplicationCopies, true);
    }
}

public sealed class ContinuousQueryView : RequestView
{
    private readonly DataGrid grid = GridFactory.Create();
    private readonly TextBox queryText = new() { IsReadOnly = true, AcceptsReturn = true, Height = 140, FontFamily = "Consolas,Menlo,monospace" };
    private readonly Button create = ToolButton("Create", "CreateContinuousQuery", "Create a continuous query");
    private readonly Button drop = ToolButton("Drop", "DropContinuousQuery", "Drop the selected continuous query");
    private readonly Button backfill = ToolButton("Backfill", "BackFill", "Run a backfill");

    public Tickbird.Core.Data.DatabaseClient? BackfillClient => Client;

    public CQBackfillHandler? RunBackfill { get; set; }

    public delegate Task CQBackfillHandler();

    public ContinuousQueryView()
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(6) };
        header.Children.Add(create); header.Children.Add(drop); header.Children.Add(backfill);
        GridFactory.SetColumns(grid, new[] { "Name" });
        grid.SelectionMode = DataGridSelectionMode.Single;
        var panel = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(queryText, Dock.Bottom);
        panel.Children.Add(header);
        panel.Children.Add(queryText);
        panel.Children.Add(grid);
        Content = TickbirdTheme.Card(panel);

        grid.SelectionChanged += (_, _) => { UpdateButtons(); queryText.Text = Selected?.Query; };
        create.Click += async (_, _) => await Guard(CreateAsync);
        drop.Click += async (_, _) => await Guard(DropAsync);
        backfill.Click += async (_, _) => { if (RunBackfill != null) await Guard(() => RunBackfill()); };
        UpdateButtons();
    }

    private ContinuousQuery? Selected => (grid.SelectedItem as GridRow)?.Tag as ContinuousQuery;

    private void UpdateButtons() => drop.IsEnabled = Selected != null;

    public override async Task ExecuteAsync()
    {
        var keep = Selected?.Name;
        queryText.Text = null;
        if (string.IsNullOrEmpty(Database)) return;
        var cqs = (await Client.GetContinousQueriesAsync(Database) ?? Enumerable.Empty<ContinuousQuery>()).ToList();
        var rows = cqs.Select(c => new GridRow(new[] { c.Name }, c)).ToList();
        grid.ItemsSource = rows;
        grid.SelectedItem = rows.FirstOrDefault(r => ((ContinuousQuery)r.Tag!).Name == keep);
        UpdateButtons();
    }

    private async Task CreateAsync()
    {
        if (OwnerWindow == null || Database == null) return;
        var cq = await TickbirdDialogs.CreateContinuousQueryAsync(OwnerWindow, Client, Database);
        if (cq == null) return;
        var response = await Client.CreateContinuousQueryAsync(cq);
        if (!response.Success) await ShowError(response.Body);
        await ExecuteAsync();
    }

    private async Task DropAsync()
    {
        var cq = Selected;
        if (OwnerWindow == null || Database == null || cq == null) return;
        if (!await Dialogs.ConfirmAsync(OwnerWindow, "Confirm Drop", $"Drop continuous query: {cq.Name}?")) return;
        var response = await Client.DropContinuousQueryAsync(Database, cq.Name);
        if (!response.Success) await ShowError(response.Body);
        await ExecuteAsync();
    }
}

public sealed class UsersView : RequestView
{
    private readonly DataGrid users = GridFactory.Create();
    private readonly DataGrid grants = GridFactory.Create();
    private readonly Button createUser = ToolButton("Create User", "CreateUser");
    private readonly Button editUser = ToolButton("Edit", "EditUser");
    private readonly Button password = ToolButton("Password", "Password");
    private readonly Button dropUser = ToolButton("Drop", "DropUser");
    private readonly Button grant = ToolButton("Grant", "GrantPrivilege");
    private readonly Button editGrant = ToolButton("Edit Privilege", "EditPrivilege");
    private readonly Button revoke = ToolButton("Revoke", "DropUser");

    public UsersView()
    {
        GridFactory.SetColumns(users, new[] { "User", "Admin" });
        GridFactory.SetColumns(grants, new[] { "Database", "Read", "Write", "All" });
        users.SelectionMode = grants.SelectionMode = DataGridSelectionMode.Single;

        var userBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(6), Children = { createUser, editUser, password, dropUser } };
        var grantBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(6), Children = { grant, editGrant, revoke } };
        var left = new DockPanel(); DockPanel.SetDock(userBar, Dock.Top); left.Children.Add(userBar); left.Children.Add(users);
        var right = new DockPanel(); DockPanel.SetDock(grantBar, Dock.Top); right.Children.Add(grantBar); right.Children.Add(grants);
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,5,*") };
        Grid.SetColumn(right, 2);
        var splitter = new GridSplitter { HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetColumn(splitter, 1);
        layout.Children.Add(left); layout.Children.Add(splitter); layout.Children.Add(right);
        Content = TickbirdTheme.Card(layout);

        users.SelectionChanged += async (_, _) => { UpdateButtons(); await Guard(BindPrivilegesAsync); };
        grants.SelectionChanged += (_, _) => UpdateButtons();
        createUser.Click += async (_, _) => await Guard(CreateUserAsync);
        editUser.Click += async (_, _) => await Guard(EditUserAsync);
        password.Click += async (_, _) => await Guard(PasswordAsync);
        dropUser.Click += async (_, _) => await Guard(DropUserAsync);
        grant.Click += async (_, _) => await Guard(GrantAsync);
        editGrant.Click += async (_, _) => await Guard(EditGrantAsync);
        revoke.Click += async (_, _) => await Guard(RevokeAsync);
        UpdateButtons();
    }

    private DatabaseUser? SelectedUser => (users.SelectedItem as GridRow)?.Tag as DatabaseUser;
    private DatabaseGrant? SelectedGrant => (grants.SelectedItem as GridRow)?.Tag as DatabaseGrant;

    private void UpdateButtons()
    {
        var u = SelectedUser;
        editUser.IsEnabled = password.IsEnabled = dropUser.IsEnabled = u != null;
        grant.IsEnabled = u != null && !u.IsAdmin;
        editGrant.IsEnabled = revoke.IsEnabled = SelectedGrant != null;
        grants.IsEnabled = u != null && !u.IsAdmin;
    }

    public override async Task ExecuteAsync()
    {
        var keep = SelectedUser?.Name;
        var list = (await Client.GetUsersAsync() ?? Enumerable.Empty<DatabaseUser>()).ToList();
        var rows = list.Select(u => new GridRow(new[] { u.Name, u.IsAdmin ? GridFactory.CheckMark : null }, u)).ToList();
        users.ItemsSource = rows;
        users.SelectedItem = rows.FirstOrDefault(r => ((DatabaseUser)r.Tag!).Name == keep);
        UpdateButtons();
    }

    private async Task BindPrivilegesAsync()
    {
        var keep = SelectedGrant?.Database;
        var user = SelectedUser;
        if (user == null || user.IsAdmin) { grants.ItemsSource = null; UpdateButtons(); return; }
        var privileges = (await Client.GetPrivilegesAsync(user.Name) ?? Enumerable.Empty<DatabaseGrant>()).ToList();
        var rows = privileges.Select(p => new GridRow(new[]
        {
            p.Database,
            p.Privilege == DatabasePrivileges.Read ? GridFactory.CheckMark : null,
            p.Privilege == DatabasePrivileges.Write ? GridFactory.CheckMark : null,
            p.Privilege == DatabasePrivileges.All ? GridFactory.CheckMark : null,
        }, p)).ToList();
        grants.ItemsSource = rows;
        grants.SelectedItem = rows.FirstOrDefault(r => ((DatabaseGrant)r.Tag!).Database == keep);
        UpdateButtons();
    }

    private async Task Report(CommandResult response)
    {
        if (!response.Success) await ShowError(response.Body);
    }

    private async Task CreateUserAsync()
    {
        if (OwnerWindow == null) return;
        var input = await TickbirdDialogs.CreateUserAsync(OwnerWindow);
        if (input == null) return;
        if (users.ItemsSource is IEnumerable<GridRow> existing && existing.Any(r => r[0] == input.Name))
        { await ShowError("User already exists: " + input.Name, "Create Error"); return; }
        await Report(await Client.CreateUserAsync(input.Name, input.Password, input.IsAdmin));
        await ExecuteAsync();
    }

    private async Task EditUserAsync()
    {
        var user = SelectedUser;
        if (OwnerWindow == null || user == null) return;
        var admin = await TickbirdDialogs.EditUserAsync(OwnerWindow, user);
        if (admin == null || admin == user.IsAdmin) return;
        await Report(admin.Value ? await Client.GrantAdministratorAsync(user.Name) : await Client.RevokeAdministratorAsync(user.Name));
        await ExecuteAsync();
    }

    private async Task PasswordAsync()
    {
        var user = SelectedUser;
        if (OwnerWindow == null || user == null) return;
        var newPassword = await TickbirdDialogs.PasswordAsync(OwnerWindow, user);
        if (newPassword == null) return;
        await Report(await Client.SetPasswordAsync(user.Name, newPassword));
    }

    private async Task DropUserAsync()
    {
        var user = SelectedUser;
        if (OwnerWindow == null || user == null) return;
        if (!await Dialogs.ConfirmAsync(OwnerWindow, "Confirm Drop", $"Drop user: {user.Name}?")) return;
        await Report(await Client.DropUserAsync(user.Name));
        await ExecuteAsync();
    }

    private async Task GrantAsync()
    {
        var user = SelectedUser;
        if (OwnerWindow == null || user == null) return;
        var input = await TickbirdDialogs.GrantPrivilegeAsync(OwnerWindow, user, await Client.GetDatabaseNamesAsync());
        if (input == null) return;
        await Report(await Client.GrantPrivilegeAsync(user.Name, input.Privilege, input.Database));
        await BindPrivilegesAsync();
    }

    private async Task EditGrantAsync()
    {
        var user = SelectedUser; var current = SelectedGrant;
        if (OwnerWindow == null || user == null || current == null) return;
        var privilege = await TickbirdDialogs.EditPrivilegeAsync(OwnerWindow, user.Name, current);
        if (privilege == null || privilege == current.Privilege) return;
        if (privilege == DatabasePrivileges.None) await Report(await Client.RevokePrivilegeAsync(user.Name, current.Privilege, current.Database));
        else await Report(await Client.GrantPrivilegeAsync(user.Name, privilege.Value, current.Database));
        await BindPrivilegesAsync();
    }

    private async Task RevokeAsync()
    {
        var user = SelectedUser; var current = SelectedGrant;
        if (OwnerWindow == null || user == null || current == null) return;
        if (!await Dialogs.ConfirmAsync(OwnerWindow, "Confirm Revoke", $"Revoke {current.Privilege} on {current.Database} from {user.Name}?")) return;
        await Report(await Client.RevokePrivilegeAsync(user.Name, current.Privilege, current.Database));
        await BindPrivilegesAsync();
    }
}

public sealed class StatsView : RequestView
{
    private readonly ComboBox categories = new() { MinWidth = 220 };
    private readonly TabControl series = new();
    private ServerStats? stats;
    private bool binding;

    public StatsView()
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(6) };
        header.Children.Add(new TextBlock { Text = "Statistic:", VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(categories);
        var refresh = ToolButton("Refresh", "Refresh");
        refresh.Click += async (_, _) => await Guard(ExecuteAsync);
        header.Children.Add(refresh);
        var panel = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        panel.Children.Add(series);
        Content = TickbirdTheme.Card(panel);
        categories.SelectionChanged += (_, _) => { if (!binding) BindSelected(); };
    }

    private IEnumerable<ResultSeries>? Series(string name) =>
        stats != null && stats.Custom.TryGetValue(name, out var custom) ? custom
            : stats?.GetType().GetProperty(name)?.GetValue(stats) as IEnumerable<ResultSeries>;

    public override async Task ExecuteAsync()
    {
        var keep = categories.SelectedItem as string;
        stats = await Client.GetStatsAsync();
        var names = stats == null ? new List<string>() : stats.GetType().GetProperties()
            .Where(p => p.GetValue(stats) is IEnumerable<ResultSeries> s && s.Any()).Select(p => p.Name).ToList();
        if (stats != null) names.AddRange(stats.Custom.Where(c => c.Value.Any()).Select(c => c.Key));
        binding = true;
        categories.ItemsSource = names;
        categories.SelectedItem = keep != null && names.Contains(keep) ? keep : names.FirstOrDefault();
        binding = false;
        BindSelected();
    }

    private void BindSelected()
    {
        series.Items.Clear();
        if (categories.SelectedItem is not string name) return;
        var count = 0;
        foreach (var s in Series(name) ?? Enumerable.Empty<ResultSeries>())
        {
            var view = new ResultsView { Client = Client };
            view.Show(s);
            series.Items.Add(new TabItem { Header = $"{s.Name} {++count}", Content = view });
        }
    }
}

public sealed class DiagnosticsView : RequestView
{
    private readonly DataGrid grid = GridFactory.Create();

    public DiagnosticsView()
    {
        GridFactory.SetColumns(grid, new[] { "Property", "Value" });
        var refresh = ToolButton("Refresh", "Refresh");
        refresh.Click += async (_, _) => await Guard(ExecuteAsync);
        var header = new StackPanel { Margin = new Thickness(6), Children = { refresh } };
        var panel = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        panel.Children.Add(grid);
        Content = TickbirdTheme.Card(panel);
    }

    public override async Task ExecuteAsync()
    {
        var d = await Client.GetDiagnosticsAsync();
        const string blank = "-";
        string V(string? s) => string.IsNullOrEmpty(s) ? blank : s;
        var ut = d?.Uptime ?? TimeSpan.Zero;
        var rows = d == null ? new List<(string, string)>()
            : d.Properties != null ? d.Properties.Select(p => (p.Key, V(p.Value))).ToList()
            : new List<(string, string)>
        {
            ("Hostname", V(d.Hostname)), ("PID", d.PID.ToString()), ("Current time", d.CurrentTime.ToString()),
            ("Started", d.Started.ToString()), ("Uptime", $"{ut.Days}d {ut.Hours}h {ut.Minutes}m {ut.Seconds}s {ut.Milliseconds}ms"),
            ("Branch", V(d.Branch)), ("Commit", V(d.Commit)), ("Build version", V(d.BuildVersion)), ("Go arch", V(d.GoArch)),
            ("Go max procs", d.GoMaxProc.ToString()), ("Go OS", V(d.GoOs)), ("Go version", V(d.GoVersion)),
        };
        grid.ItemsSource = rows.Select(r => new GridRow(new[] { r.Item1, r.Item2 })).ToList();
    }
}
