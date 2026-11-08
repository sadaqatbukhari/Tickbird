using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Tickbird.Core.Data;
using Tickbird.Core.Settings;
using Tickbird.Desktop.Services;

namespace Tickbird.Desktop.Tests;

public sealed class ConnectionDialogTests
{
    [AvaloniaFact]
    public async Task RedesignedEditor_PreservesConnectionValuesOnSave()
    {
        var owner = new Window();
        owner.Show();
        try
        {
            var existing = new DatabaseConnection("test", "New Connection", "192.168.102.31", 8086,
                "user", "secret", true, "db") { Group = "DEV" };
            var pending = TickbirdDialogs.ConnectionAsync(owner, existing, new TickbirdSettings { Groups = { "DEV" } });
            if (pending.IsFaulted) await pending;
            var dialog = Assert.IsType<FormDialog>(Assert.Single(owner.OwnedWindows));
            dialog.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var toggle = Assert.Single(dialog.GetLogicalDescendants().OfType<ToggleSwitch>());
            Assert.True(toggle.IsChecked);
            var host = Assert.Single(dialog.GetLogicalDescendants().OfType<TextBox>(), t => t.Name == "ConnectionHost");
            Assert.True(host.IsEffectivelyVisible);
            Assert.True(host.Bounds.Width > 150);
            var port = Assert.Single(dialog.GetLogicalDescendants().OfType<NumericUpDown>());
            var portText = Assert.Single(port.GetVisualDescendants().OfType<TextBox>());
            Assert.True(portText.Bounds.Width >= 55, $"Port text area is too narrow: {portText.Bounds.Width}");
            var secret = Assert.Single(dialog.GetLogicalDescendants().OfType<TextBox>(), t => t.Name == "ConnectionSecret");
            var eye = Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), b => b.Name == "PasswordVisibilityButton");
            Assert.NotEqual('\0', secret.PasswordChar);
            eye.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal('\0', secret.PasswordChar);
            Assert.Equal(existing.Password, secret.Text);
            eye.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.NotEqual('\0', secret.PasswordChar);
            var screenshotDir = Environment.GetEnvironmentVariable("INFLUX_SCREENSHOT_DIR");
            if (!string.IsNullOrEmpty(screenshotDir))
            {
                Directory.CreateDirectory(screenshotDir);
                dialog.CaptureRenderedFrame()?.Save(Path.Combine(screenshotDir, "edit-connection.png"));
            }
            var save = Assert.Single(dialog.GetLogicalDescendants().OfType<Button>(), b => b.IsDefault);
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var updated = await pending;
            Assert.NotNull(updated);
            Assert.Equal(existing.Id, updated.Id);
            Assert.Equal(existing.Name, updated.Name);
            Assert.Equal(existing.Group, updated.Group);
            Assert.Equal(existing.Host, updated.Host);
            Assert.Equal(existing.Port, updated.Port);
            Assert.Equal(existing.Database, updated.Database);
            Assert.Equal(existing.Username, updated.Username);
            Assert.Equal(existing.Password, updated.Password);
            Assert.Equal(existing.UseSsl, updated.UseSsl);
            Assert.Equal(existing.ServerVersion, updated.ServerVersion);
        }
        finally { owner.Close(); }
    }
}
