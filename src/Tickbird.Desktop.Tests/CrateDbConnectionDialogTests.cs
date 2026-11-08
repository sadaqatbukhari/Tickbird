using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Tickbird.Core.Data;
using Tickbird.Core.Providers;
using Tickbird.Core.Settings;
using Tickbird.Desktop.Services;

namespace Tickbird.Desktop.Tests;

/// <summary>The connection dialog for CrateDB: no Database field, a starting user, a note, and a port check.</summary>
public class CrateDbConnectionDialogTests
{
    private static async Task<(Window Owner, FormDialog Dialog, Task<DatabaseConnection?> Result)> Open(DatabaseConnection? existing)
    {
        var owner = new Window();
        owner.Show();
        var result = TickbirdDialogs.ConnectionAsync(owner, existing, new TickbirdSettings());
        if (result.IsFaulted) await result;
        var dialog = Assert.IsType<FormDialog>(Assert.Single(owner.OwnedWindows));
        dialog.UpdateLayout();
        Pump();
        return (owner, dialog, result);
    }

    private static void Pump() { for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs(); }

    private static ComboBox ServerBox(FormDialog dialog) =>
        dialog.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Items.Cast<object>().Contains("CrateDB"));

    private static void Choose(FormDialog dialog, string name)
    {
        var box = ServerBox(dialog);
        box.SelectedIndex = box.Items.Cast<object>().ToList().IndexOf(name);
        Pump();
    }

    private static TextBox Database(FormDialog d) => d.GetLogicalDescendants().OfType<TextBox>().Single(t => t.Watermark == "Enter database name");
    private static TextBox User(FormDialog d) => d.GetLogicalDescendants().OfType<TextBox>().Single(t => t.Watermark == "Enter username");
    private static TextBox Host(FormDialog d) => d.GetLogicalDescendants().OfType<TextBox>().Single(t => t.Name == "ConnectionHost");
    private static TextBlock DatabaseLabel(FormDialog d) => d.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Name == "ConnectionDatabaseLabel");
    private static TextBlock Hint(FormDialog d) => d.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Name == "ConnectionHint");
    private static NumericUpDown Port(FormDialog d) => d.GetLogicalDescendants().OfType<NumericUpDown>().Single();

    private static async Task<DatabaseConnection?> Save(FormDialog dialog, Task<DatabaseConnection?> result)
    {
        var save = Assert.Single(dialog.GetLogicalDescendants().OfType<Button>(), b => b.IsDefault);
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return await result;
    }

    [AvaloniaFact]
    public async Task CrateDb_IsInTheServerList()
    {
        var (owner, dialog, _) = await Open(null);
        try { Assert.Contains("CrateDB", ServerBox(dialog).Items.Cast<string>()); }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task ChoosingCrateDb_HidesTheDatabaseField_ShowsTheNote_AndStartsWithTheCrateUser()
    {
        var (owner, dialog, _) = await Open(null);
        try
        {
            Assert.True(Database(dialog).IsVisible);
            Assert.True(DatabaseLabel(dialog).IsVisible);
            Assert.False(Hint(dialog).IsVisible);
            Assert.Equal(8086, (int)Port(dialog).Value!);

            Choose(dialog, "CrateDB");

            Assert.False(Database(dialog).IsVisible);
            Assert.False(DatabaseLabel(dialog).IsVisible);
            Assert.True(Hint(dialog).IsVisible);
            Assert.Contains("PostgreSQL protocol", Hint(dialog).Text);
            Assert.Equal("crate", User(dialog).Text);
            Assert.Equal(5432, (int)Port(dialog).Value!);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task GoingBack_ShowsTheFieldAgain_AndClearsTheStartingUser()
    {
        var (owner, dialog, _) = await Open(null);
        try
        {
            Choose(dialog, "CrateDB");
            Choose(dialog, "InfluxDB 1.x");

            Assert.True(Database(dialog).IsVisible);
            Assert.True(DatabaseLabel(dialog).IsVisible);
            Assert.False(Hint(dialog).IsVisible);
            Assert.True(string.IsNullOrEmpty(User(dialog).Text));
            Assert.Equal(8086, (int)Port(dialog).Value!);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task ATypedUser_IsKept_WhenTheDatabaseChanges()
    {
        var (owner, dialog, _) = await Open(null);
        try
        {
            User(dialog).Text = "alice";
            Choose(dialog, "CrateDB");
            Assert.Equal("alice", User(dialog).Text);
            Choose(dialog, "TimescaleDB");
            Assert.Equal("alice", User(dialog).Text);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task ACrateDbConnection_IsSaved_WithoutADatabaseName()
    {
        var (owner, dialog, result) = await Open(null);
        try
        {
            Database(dialog).Text = "typed-before-switching";
            Choose(dialog, "CrateDB");
            var connection = await Save(dialog, result);

            Assert.Equal(DatabaseKind.CrateDb, connection!.Kind);
            Assert.Equal("crate", connection.Username);
            Assert.Equal(5432, connection.Port);
            Assert.Null(connection.Database);        // the field is hidden, so what was typed in it is not kept
            Assert.Null(connection.Token);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task TheHttpPort_IsRefusedForCrateDb_ButNotForOthers()
    {
        var (owner, dialog, _) = await Open(null);
        try
        {
            Choose(dialog, "CrateDB");
            Assert.Null(dialog.Validate!());
            Port(dialog).Value = 4200;
            Assert.Contains("HTTP port", dialog.Validate!());

            Choose(dialog, "TimescaleDB");
            Assert.Equal(4200, (int)Port(dialog).Value!);          // a typed port is kept...
            Assert.Null(dialog.Validate!());                        // ...and is fine for a database that has no such rule
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task ANameAndAHost_AreStillRequired()
    {
        var (owner, dialog, _) = await Open(null);
        try
        {
            Choose(dialog, "CrateDB");
            Host(dialog).Text = " ";
            Assert.Equal("Host cannot be blank.", dialog.Validate!());
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task EditingACrateDbConnection_ShowsItsServer_AndKeepsTheFieldHidden()
    {
        var existing = new DatabaseConnection("c", "Prod crate", "crate.example.com", 5432, "app", "pw", true) { Kind = DatabaseKind.CrateDb };
        var (owner, dialog, result) = await Open(existing);
        try
        {
            Assert.Equal("CrateDB", ServerBox(dialog).SelectedItem);
            Assert.False(Database(dialog).IsVisible);
            Assert.True(Hint(dialog).IsVisible);
            Assert.Equal("app", User(dialog).Text);            // the saved user is not replaced by the starting one
            var saved = await Save(dialog, result);
            Assert.Equal(DatabaseKind.CrateDb, saved!.Kind);
            Assert.Equal("app", saved.Username);
            Assert.Equal("pw", saved.Password);
            Assert.True(saved.UseSsl);
        }
        finally { owner.Close(); }
    }
}
