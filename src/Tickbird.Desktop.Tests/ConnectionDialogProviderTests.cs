using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tickbird.Core.Data;
using Tickbird.Core.Providers;
using Tickbird.Core.Settings;
using Tickbird.Desktop.Services;

namespace Tickbird.Desktop.Tests;

/// <summary>The connection dialog lists the registered databases and adapts its fields to the chosen one.</summary>
public class ConnectionDialogProviderTests
{
    private static async Task<(Window Owner, FormDialog Dialog, Task<DatabaseConnection?> Result)> Open(DatabaseConnection? existing)
    {
        var owner = new Window();
        owner.Show();
        var result = TickbirdDialogs.ConnectionAsync(owner, existing, new TickbirdSettings());
        if (result.IsFaulted) await result;
        var dialog = Assert.IsType<FormDialog>(Assert.Single(owner.OwnedWindows));
        dialog.UpdateLayout();
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
        return (owner, dialog, result);
    }

    private static ComboBox ServerBox(FormDialog dialog) =>
        dialog.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Items.Cast<object>().Contains("TimescaleDB"));

    private static void Choose(FormDialog dialog, string name)
    {
        var box = ServerBox(dialog);
        box.SelectedIndex = box.Items.Cast<object>().ToList().IndexOf(name);
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
    }

    private static async Task<DatabaseConnection?> Save(FormDialog dialog, Task<DatabaseConnection?> result)
    {
        var save = Assert.Single(dialog.GetLogicalDescendants().OfType<Button>(), b => b.IsDefault);
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return await result;
    }

    [AvaloniaFact]
    public async Task TheServerList_ComesFromTheRegistry()
    {
        var (owner, dialog, _) = await Open(null);
        try
        {
            Assert.Equal(DatabaseProfiles.All.Select(p => p.DisplayName), ServerBox(dialog).Items.Cast<string>());
            Assert.Equal("InfluxDB 1.x", ServerBox(dialog).SelectedItem);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task ChoosingTimescaleDb_MovesThePort_AndKeepsUserAndPassword()
    {
        var (owner, dialog, result) = await Open(null);
        try
        {
            var port = dialog.GetLogicalDescendants().OfType<NumericUpDown>().Single();
            var username = dialog.GetLogicalDescendants().OfType<TextBox>().Single(t => t.Watermark == "Enter username");
            Assert.Equal(8086, (int)port.Value!);

            Choose(dialog, "TimescaleDB");

            Assert.Equal(5432, (int)port.Value!);
            Assert.True(username.IsVisible);
            username.Text = "app";
            var connection = await Save(dialog, result);
            Assert.Equal(DatabaseKind.TimescaleDb, connection!.Kind);
            Assert.Equal("app", connection.Username);
            Assert.Equal(5432, connection.Port);
            Assert.Null(connection.Token);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task ChoosingInfluxDb3_HidesTheUser_AndSavesAToken()
    {
        var (owner, dialog, result) = await Open(null);
        try
        {
            var username = dialog.GetLogicalDescendants().OfType<TextBox>().Single(t => t.Watermark == "Enter username");
            var secret = dialog.GetLogicalDescendants().OfType<TextBox>().Single(t => t.Name == "ConnectionSecret");
            Choose(dialog, "InfluxDB 3.x");
            Assert.False(username.IsVisible);
            secret.Text = "tok";

            var connection = await Save(dialog, result);

            Assert.Equal(DatabaseKind.InfluxDb3, connection!.Kind);
            Assert.Equal(InfluxDbServerVersion.InfluxDb3, connection.ServerVersion);
            Assert.Equal("tok", connection.Token);
            Assert.Null(connection.Password);
            Assert.Equal(8181, connection.Port);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task AUserTypedPort_IsKeptWhenTheDatabaseChanges()
    {
        var (owner, dialog, _) = await Open(null);
        try
        {
            var port = dialog.GetLogicalDescendants().OfType<NumericUpDown>().Single();
            port.Value = 9999;
            Choose(dialog, "TimescaleDB");
            Assert.Equal(9999, (int)port.Value!);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task EditingATimescaleConnection_ShowsItsServer_AndKeepsTheKind()
    {
        var existing = new DatabaseConnection("t", "Prod TS", "db.example.com", 5432, "app", "pw", true, "metrics") { Kind = DatabaseKind.TimescaleDb };
        var (owner, dialog, result) = await Open(existing);
        try
        {
            Assert.Equal("TimescaleDB", ServerBox(dialog).SelectedItem);
            var saved = await Save(dialog, result);
            Assert.Equal(DatabaseKind.TimescaleDb, saved!.Kind);
            Assert.Equal("metrics", saved.Database);
            Assert.Equal("pw", saved.Password);
        }
        finally { owner.Close(); }
    }
}
