using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Tickbird.Core.Data;
using Tickbird.Core.Providers;
using Tickbird.Core.Navigation;
using Tickbird.Core.Query;
using Tickbird.Core.Settings;

namespace Tickbird.Desktop.Services;

public sealed record RetentionPolicyInput(string Name, string Duration, int Replication, bool IsDefault);
public sealed record UserInput(string Name, string Password, bool IsAdmin);
public sealed record PrivilegeInput(string Database, DatabasePrivileges Privilege);

/// <summary>The application's dialogs (ports of the WinForms dialog classes).</summary>
public static class TickbirdDialogs
{
    public static async Task<string?> CreateDatabaseAsync(Window owner, string connectionName, string? initialName, IEnumerable<string> existing)
    {
        var name = Ui.Text(initialName);
        var dialog = new FormDialog("Create Database", "Create",
            ("Connection:", new TextBlock { Text = connectionName }),
            ("Database name:", name));
        dialog.Validate = () => Validation.Database(name.Text, existing);
        return await dialog.ShowAsync(owner) ? name.Text!.Trim() : null;
    }

    public static async Task<RetentionPolicyInput?> RetentionPolicyAsync(Window owner, RetentionPolicy? existing, DatabaseProfile? profile = null)
    {
        var creating = existing == null;
        var name = Ui.Text(existing?.Name);
        name.IsReadOnly = !creating;
        var duration = Ui.Text(existing?.Duration, watermark: "e.g. 30d, 12h, INF");
        if (profile?.RetentionPerTable == true)
        {
            // The policy belongs to one table: only the table and how long its data is kept.
            name.Watermark = "table, or schema.table";
            duration.Watermark = "e.g. 30d, 12h, 1w";
            var perTable = new FormDialog(creating ? "Create Retention Policy" : "Alter Retention Policy", creating ? "Create" : "Update",
                ("Table:", name), ("Keep data for:", duration));
            perTable.Validate = () => string.IsNullOrWhiteSpace(name.Text) ? "Table name cannot be blank."
                : Validation.RetentionPolicy(name.Text, duration.Text);
            if (!await perTable.ShowAsync(owner)) return null;
            return new RetentionPolicyInput(name.Text!.Trim(), duration.Text!.Trim(), 1, false);
        }
        var replication = Ui.Number(existing?.ReplicationCopies ?? 1, 1, 100);
        var isDefault = new CheckBox { IsChecked = existing?.Default ?? false, Content = "Make this the default policy" };
        var dialog = new FormDialog(creating ? "Create Retention Policy" : "Alter Retention Policy", creating ? "Create" : "Update",
            ("Name:", name), ("Duration:", duration), ("Replication:", replication), ("", isDefault));
        dialog.Validate = () => Validation.RetentionPolicy(name.Text, duration.Text);
        if (!await dialog.ShowAsync(owner)) return null;
        return new RetentionPolicyInput(name.Text!.Trim(), duration.Text!.Trim(), (int)(replication.Value ?? 1), isDefault.IsChecked == true);
    }

    public static async Task<UserInput?> CreateUserAsync(Window owner, bool allowAdministrator = true)
    {
        var name = Ui.Text();
        var password = Ui.Text(password: true);
        var admin = new CheckBox { Content = "Administrator" };
        // A database that cannot make administrators (CrateDB) has no such checkbox.
        var dialog = allowAdministrator
            ? new FormDialog("Create User", "Create", ("User name:", name), ("Password:", password), ("", admin))
            : new FormDialog("Create User", "Create", ("User name:", name), ("Password:", password));
        dialog.Validate = () => Validation.User(name.Text, password.Text);
        return await dialog.ShowAsync(owner) ? new UserInput(name.Text!.Trim(), password.Text!, admin.IsChecked == true) : null;
    }

    /// <summary>Returns the new administrator flag, or null when cancelled.</summary>
    public static async Task<bool?> EditUserAsync(Window owner, DatabaseUser user)
    {
        var admin = new CheckBox { Content = "Administrator", IsChecked = user.IsAdmin };
        var dialog = new FormDialog("Edit User", "Update", ("User name:", new TextBlock { Text = user.Name }), ("", admin));
        return await dialog.ShowAsync(owner) ? admin.IsChecked == true : null;
    }

    public static async Task<string?> PasswordAsync(Window owner, DatabaseUser user)
    {
        var password = Ui.Text(password: true);
        var dialog = new FormDialog("Change Password", "Update", ("User name:", new TextBlock { Text = user.Name }), ("New password:", password));
        dialog.Validate = () => string.IsNullOrWhiteSpace(password.Text) ? "Password cannot be blank." : null;
        return await dialog.ShowAsync(owner) ? password.Text : null;
    }

    public static async Task<PrivilegeInput?> GrantPrivilegeAsync(Window owner, DatabaseUser user, IEnumerable<string> databases)
    {
        var db = new ComboBox { ItemsSource = databases.ToList(), SelectedIndex = 0 };
        var privilege = new ComboBox
        {
            ItemsSource = Enum.GetValues<DatabasePrivileges>().Where(p => p != DatabasePrivileges.None).ToList(),
            SelectedIndex = 0
        };
        var dialog = new FormDialog("Grant Privilege", "Grant",
            ("User name:", new TextBlock { Text = user.Name }), ("Database:", db), ("Privilege:", privilege));
        dialog.Validate = () => db.SelectedItem == null ? "Select a database." : null;
        return await dialog.ShowAsync(owner)
            ? new PrivilegeInput((string)db.SelectedItem!, (DatabasePrivileges)privilege.SelectedItem!) : null;
    }

    public static async Task<DatabasePrivileges?> EditPrivilegeAsync(Window owner, string username, DatabaseGrant grant)
    {
        var privilege = new ComboBox { ItemsSource = Enum.GetValues<DatabasePrivileges>().ToList(), SelectedItem = grant.Privilege };
        var dialog = new FormDialog("Edit Privilege", "Update",
            ("User name:", new TextBlock { Text = username }), ("Database:", new TextBlock { Text = grant.Database }), ("Privilege:", privilege));
        return await dialog.ShowAsync(owner) ? (DatabasePrivileges)privilege.SelectedItem! : null;
    }

    public static async Task<ContinuousQueryParams?> CreateContinuousQueryAsync(Window owner, DatabaseClient client, string database)
    {
        var measurements = (await client.GetMeasurementNamesAsync(database) ?? Enumerable.Empty<string>()).ToList();
        var profile = client.Connection.Profile;
        if (!profile.ContinuousQueryWritesToDestination)
        {
            // A continuous aggregate: a named view over one table, grouped by time bucket (and tags), kept up to date.
            var aggregateName = Ui.Text();
            var from = new AutoCompleteBox { ItemsSource = measurements, FilterMode = AutoCompleteFilterMode.Contains };
            var bucket = Ui.Text(watermark: "e.g. 10m, 1h, 1d");
            var groupBy = Ui.Text(watermark: "comma-separated text columns");
            var aggregates = new TextBox { AcceptsReturn = true, Height = 110, Text = "avg(\"value\") AS \"value\"", FontFamily = "Consolas,Menlo,monospace" };
            var aggregateDialog = new FormDialog("Create " + profile.Terms.ContinuousQuery, "Create",
                ("Database:", new TextBlock { Text = database }), ("Name:", aggregateName), ("Source " + profile.Terms.Measurement.ToLowerInvariant() + ":", from),
                ("Bucket size:", bucket), ("Group by:", groupBy), ("Aggregates:", aggregates));
            aggregateDialog.Validate = () => Validation.ContinuousQuery(aggregateName.Text, aggregateName.Text, from.Text, bucket.Text,
                false, null, null, aggregates.Text);
            if (!await aggregateDialog.ShowAsync(owner)) return null;
            return new ContinuousQueryParams
            {
                Name = aggregateName.Text, Database = database, Source = from.Text, Destination = aggregateName.Text,
                SubQueries = new List<string> { aggregates.Text!.Trim() }, Interval = bucket.Text,
                FillType = InfluxDbFillTypes.Null, Tags = Validation.SplitList(groupBy.Text)
            };
        }
        var name = Ui.Text();
        var destination = new AutoCompleteBox { ItemsSource = measurements, FilterMode = AutoCompleteFilterMode.Contains };
        var source = new AutoCompleteBox { ItemsSource = measurements, FilterMode = AutoCompleteFilterMode.Contains };
        var interval = Ui.Text(watermark: "e.g. 10m");
        var fill = new ComboBox { ItemsSource = Enum.GetValues<InfluxDbFillTypes>().ToList(), SelectedIndex = 0 };
        var tags = Ui.Text(watermark: "comma-separated tag keys");
        var resample = new CheckBox { Content = "Resample" };
        var every = Ui.Text(); every.IsEnabled = false;
        var @for = Ui.Text(); @for.IsEnabled = false;
        resample.IsCheckedChanged += (_, _) => every.IsEnabled = @for.IsEnabled = resample.IsChecked == true;
        var query = new TextBox { AcceptsReturn = true, Height = 110, Text = Validation.SubQueryPlaceholder, FontFamily = "Consolas,Menlo,monospace" };

        var dialog = new FormDialog("Create Continuous Query", "Create",
            ("Database:", new TextBlock { Text = database }), ("Name:", name), ("Destination:", destination), ("Source:", source),
            ("Interval:", interval), ("Fill type:", fill), ("Tags:", tags), ("", resample), ("Resample every:", every),
            ("Resample for:", @for), ("SubQuery:", query));
        dialog.Validate = () => Validation.ContinuousQuery(name.Text, destination.Text, source.Text, interval.Text,
            resample.IsChecked == true, every.Text, @for.Text, query.Text);
        if (!await dialog.ShowAsync(owner)) return null;
        if (destination.Text == source.Text &&
            !await Dialogs.ConfirmAsync(owner, "Same Source and Destination",
                "Source is the same as the Destination. These are typically different values for a Continuous Query. Are you sure that you want to continue?"))
            return null;
        return new ContinuousQueryParams
        {
            Name = name.Text, Database = database, Destination = destination.Text, Source = source.Text,
            SubQueries = new List<string> { query.Text!.Trim() }, Interval = interval.Text,
            FillType = (InfluxDbFillTypes)fill.SelectedItem!, Tags = Validation.SplitList(tags.Text),
            ResampleEveryInterval = every.Text, ResampleForInterval = @for.Text
        };
    }

    public static async Task<BackfillParams?> BackfillAsync(Window owner, DatabaseClient client, string database)
    {
        var measurements = (await client.GetMeasurementNamesAsync(database) ?? Enumerable.Empty<string>()).ToList();
        var destination = new AutoCompleteBox { ItemsSource = measurements, FilterMode = AutoCompleteFilterMode.Contains };
        var source = new AutoCompleteBox { ItemsSource = measurements, FilterMode = AutoCompleteFilterMode.Contains };
        var interval = Ui.Text(watermark: "e.g. 10m");
        var fill = new ComboBox { ItemsSource = Enum.GetValues<InfluxDbFillTypes>().ToList(), SelectedIndex = 0 };
        var tags = Ui.Text(watermark: "comma-separated tag keys");
        var filters = Ui.Text(watermark: "comma-separated filters");
        var from = new DateTimeField(DateTime.Now.AddDays(-1));
        var to = new DateTimeField(DateTime.Now);
        var query = new TextBox { AcceptsReturn = true, Height = 110, Text = Validation.SubQueryPlaceholder, FontFamily = "Consolas,Menlo,monospace" };

        var dialog = new FormDialog("Run Backfill", "Run",
            ("Database:", new TextBlock { Text = database }), ("Destination:", destination), ("Source:", source),
            ("Interval:", interval), ("Fill type:", fill), ("Tags:", tags), ("Filters:", filters),
            ("From:", from.Control), ("To:", to.Control), ("SubQuery:", query));
        dialog.Validate = () => Validation.Backfill(destination.Text, source.Text, interval.Text, from.Value, to.Value, query.Text);
        if (!await dialog.ShowAsync(owner)) return null;
        return new BackfillParams
        {
            SubQueries = new List<string> { query.Text!.Trim() }, Destination = destination.Text, Source = source.Text,
            Interval = interval.Text, FromTime = from.Value, ToTime = to.Value, Filters = Validation.SplitList(filters.Text),
            Tags = Validation.SplitList(tags.Text), FillType = (InfluxDbFillTypes)fill.SelectedItem!
        };
    }

    /// <summary>Label shown in group pickers for connections that are not in a group.</summary>
    public const string NoGroup = "(No group)";

    /// <summary>Asks for a group name. Returns the trimmed name, or null if cancelled.</summary>
    public static async Task<string?> GroupNameAsync(Window owner, string title, string okText, string? current, IEnumerable<string> existing)
    {
        var name = Ui.Text(current, watermark: "e.g. PROD, DEV");
        var taken = existing.ToList();
        var dialog = new FormDialog(title, okText, ("Group name:", name));
        dialog.Validate = () => Validation.Group(name.Text, taken, current);
        return await dialog.ShowAsync(owner) ? name.Text!.Trim() : null;
    }

    /// <summary>
    /// Create or edit a connection. Returns the connection to save, or null if cancelled.
    /// <paramref name="defaultGroup"/> preselects a group for a new connection.
    /// </summary>
    public static async Task<DatabaseConnection?> ConnectionAsync(Window owner, DatabaseConnection? existing,
        TickbirdSettings settings, string? defaultGroup = null)
    {
        var name = Ui.Text(existing?.Name ?? "New Connection");
        var groupItems = new ObservableCollection<string>(new[] { NoGroup }.Concat(settings.AllGroups()));
        var group = new ComboBox { ItemsSource = groupItems, HorizontalAlignment = HorizontalAlignment.Stretch };
        group.SelectedItem = settings.FindGroup(existing != null ? existing.Group : defaultGroup) ?? NoGroup;
        var newGroup = new Button { Content = "New…", MinWidth = 64 };
        ToolTip.SetTip(newGroup, "Create a new group, for example PROD or DEVs");
        var groupRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(newGroup, 1);
        groupRow.Children.Add(group);
        groupRow.Children.Add(newGroup);
        string? SelectedGroup() => group.SelectedItem is string g && g != NoGroup ? g : null;
        // The databases come from the registry, so a new database appears here without any change to this dialog.
        var profiles = DatabaseProfiles.All;
        var server = new ComboBox
        {
            ItemsSource = profiles.Select(p => p.DisplayName).ToList(),
            SelectedIndex = Math.Max(0, profiles.ToList().FindIndex(p => p.Kind == (existing?.Kind ?? DatabaseKind.InfluxDb1)))
        };
        var host = Ui.Text(existing?.Host ?? "localhost");
        var port = Ui.Number(existing == null || existing.Port == 0 ? profiles[0].DefaultPort : existing.Port, 1, 65535);
        var database = Ui.Text(existing?.Database);
        var username = Ui.Text(existing?.Username);
        var secret = Ui.Text(existing != null && existing.Profile.Authentication == AuthenticationStyle.Token ? existing.Token : existing?.Password, password: true);
        var useSsl = new CheckBox { IsChecked = existing?.UseSsl ?? false, Content = "Use SSL" };
        var usernameLabel = new TextBlock { Text = "Username:", VerticalAlignment = VerticalAlignment.Center };
        var secretLabel = new TextBlock { Text = "Password:", VerticalAlignment = VerticalAlignment.Center };

        DatabaseProfile Selected() => profiles[Math.Max(0, server.SelectedIndex)];
        var shownProfile = Selected();
        ConnectionDialogLayout.Parts? parts = null;
        void UpdateFields(bool updatePort)
        {
            var profile = Selected();
            var token = profile.Authentication == AuthenticationStyle.Token;
            username.IsVisible = !token;
            secretLabel.Text = token ? "API Token" : "Password";
            // What changes with the database: the Database field (CrateDB has one database), a note, a starting user.
            database.IsVisible = profile.UsesDatabaseName;
            if (parts != null)
            {
                parts.DatabaseLabel.IsVisible = profile.UsesDatabaseName;
                parts.Hint.Text = profile.ConnectionHint;
                parts.Hint.IsVisible = !string.IsNullOrWhiteSpace(profile.ConnectionHint);
            }
            if (updatePort)
            {
                var previous = shownProfile.DefaultUsername;
                if (string.IsNullOrWhiteSpace(username.Text) || string.Equals(username.Text, previous, StringComparison.Ordinal))
                    username.Text = profile.DefaultUsername;
            }
            // Move the port to the new database's default, unless the user typed another one.
            if (updatePort && (int)(port.Value ?? 0) == shownProfile.DefaultPort) port.Value = profile.DefaultPort;
            shownProfile = profile;
        }
        server.SelectionChanged += (_, _) => UpdateFields(true);

        DatabaseConnection Build(string id)
        {
            var profile = Selected();
            var token = profile.Authentication == AuthenticationStyle.Token;
            return new DatabaseConnection(id, name.Text ?? "", host.Text ?? "", (ushort)(port.Value ?? profile.DefaultPort),
                token ? null : username.Text, token ? null : secret.Text, useSsl.IsChecked == true,
                profile.UsesDatabaseName && !string.IsNullOrWhiteSpace(database.Text) ? database.Text : null, InfluxDbServerVersion.InfluxDb1, token ? secret.Text : null)
            {
                Group = SelectedGroup(),
                Kind = profile.Kind
            };
        }

        var dialog = new FormDialog(existing == null ? "Create Connection" : "Edit Connection", existing == null ? "Create" : "Save",
            ("Name:", name), ("Group:", groupRow), ("Server:", server), ("Host:", host), ("Port:", port), ("Database:", database),
            ("Username:", username), ("Password:", secret), ("", useSsl));
        parts = ConnectionDialogLayout.Apply(dialog, existing == null, name, groupRow, server, host, port, database,
            username, usernameLabel, secret, secretLabel, useSsl);
        // The name and host are always required; the chosen database may add its own checks (CrateDB's port 4200 is HTTP).
        dialog.Validate = () => Selected().Validate(Build(existing?.Id ?? "validate"));
        UpdateFields(false);

        // The new group is only saved when the connection is saved.
        newGroup.Click += async (_, _) =>
        {
            var created = await GroupNameAsync(dialog, "New Group", "Create", null, groupItems.Where(g => g != NoGroup));
            if (created == null) return;
            groupItems.Add(created);
            group.SelectedItem = created;
        };

        var test = new Button { Content = ConnectionDialogLayout.ActionContent("Test connection", "M4,0 L4,4 M10,0 L10,4 M2,4 L12,4 L12,7 C12,12 2,12 2,7 Z M7,11 L7,15") };
        test.Classes.Add("editorAction");
        test.Click += async (_, _) =>
        {
            try
            {
                var problem = Selected().Validate(Build(existing?.Id ?? "validate"));
                if (problem != null) throw new InvalidOperationException(problem);
                var client = DatabaseClientFactory.Create(Build(existing?.Id ?? Guid.NewGuid().ToString()));
                if (!string.IsNullOrWhiteSpace(client.Connection.Database)) await client.GetMeasurementNamesAsync(client.Connection.Database);
                else await client.GetDatabaseNamesAsync();
                await Dialogs.ShowMessageAsync(dialog, "Success", "Connection Successful!");
            }
            catch (Exception ex) { await Dialogs.ShowMessageAsync(dialog, "Failure", ex.Message); }
        };
        var ping = new Button { Content = ConnectionDialogLayout.ActionContent("Ping", "M7,5 L7,9 M4,3 C1,5 1,9 4,11 M10,3 C13,5 13,9 10,11 M1,0 C-3,4 -3,10 1,14 M13,0 C17,4 17,10 13,14") };
        ping.Classes.Add("editorAction");
        ping.Click += async (_, _) =>
        {
            try
            {
                var problem = Selected().Validate(Build(existing?.Id ?? "validate"));
                if (problem != null) throw new InvalidOperationException(problem);
                var client = DatabaseClientFactory.Create(Build(existing?.Id ?? Guid.NewGuid().ToString()));
                var response = await client.PingAsync();
                if (!response.Success) throw new Exception("There was an error connecting to the server.");
                await Dialogs.ShowMessageAsync(dialog, "Pong", $"Response Time: {response.ResponseTime.TotalMilliseconds:0} ms\n{Selected().VersionLabel}: {response.Version}");
            }
            catch (Exception ex) { await Dialogs.ShowMessageAsync(dialog, "Failure", ex.Message); }
        };
        dialog.LeftButtons.Children.Add(test);
        dialog.LeftButtons.Children.Add(ping);

        return await dialog.ShowAsync(owner) ? Build(existing?.Id ?? Guid.NewGuid().ToString()) : null;
    }

    public static Task AboutAsync(Window owner, string version, string? updateMessage, string? releaseUrl, Action<string> openUrl)
    {
        var dialog = new Window
        {
            Title = "About Tickbird", Width = 460, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 8 };
        panel.Children.Add(new BrandLogo { Width = 280, Height = 57, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(new TextBlock { Text = "Cross-platform client for time series databases", FontSize = 15, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = "Browse, query, chart and map your time series data, and manage servers, users and retention " +
                   "policies. Supports InfluxDB 1.x (InfluxQL), InfluxDB 3.x (SQL), TimescaleDB and CrateDB, with more databases planned.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = 0.85
        });
        panel.Children.Add(new TextBlock { Text = "Version " + version, Margin = new Thickness(0, 6, 0, 0) });
        panel.Children.Add(new TextBlock { Text = "Released under the MIT License. Runs on Windows, macOS and Linux.", Opacity = 0.85, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        // The warranty and liability notice (Tickbird.Core.Legal), in smaller type under the licence line.
        panel.Children.Add(new TextBlock
        {
            Name = "AboutWarranty", Text = Tickbird.Core.Legal.Warranty, FontSize = 11, Opacity = 0.75,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0)
        });
        panel.Children.Add(new TextBlock
        {
            Name = "AboutLiability", Text = Tickbird.Core.Legal.Liability, FontSize = 11, Opacity = 0.75,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        if (!string.IsNullOrEmpty(updateMessage)) panel.Children.Add(new TextBlock { Text = updateMessage, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(releaseUrl))
        {
            var link = new Button { Content = "View release" };
            link.Click += (_, _) => openUrl(releaseUrl);
            panel.Children.Add(link);
        }
        var project = new Button { Content = "Project on GitHub" };
        project.Click += (_, _) => openUrl("https://github.com/sadaqatbukhari/Tickbird");
        var close = new Button { Content = "Close", IsDefault = true };
        close.Click += (_, _) => dialog.Close();
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0), Children = { project, close }
        });
        dialog.Content = TickbirdTheme.Card(panel);
        return dialog.ShowDialog(owner);
    }
}

/// <summary>A date + time editor pair.</summary>
public sealed class DateTimeField
{
    private readonly CalendarDatePicker date = new();
    private readonly TimePicker time = new() { ClockIdentifier = "24HourClock" };

    public DateTimeField(DateTime initial)
    {
        date.SelectedDate = initial.Date;
        time.SelectedTime = initial.TimeOfDay;
        Control = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { date, time } };
    }

    public Control Control { get; }

    public DateTime Value => (date.SelectedDate ?? DateTime.Today).Date + (time.SelectedTime ?? TimeSpan.Zero);
}
