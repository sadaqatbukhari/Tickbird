using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Tickbird.Core.Data;
using Tickbird.Core.Providers;
using Tickbird.Core.Settings;
using Tickbird.Desktop.Services;

namespace Tickbird.Desktop.Tests;

/// <summary>Runs a connection test from a function, so the dialog is tested without a network.</summary>
internal sealed class FakeTester : IConnectionTester
{
    private readonly Func<DatabaseConnection, DiagnosticReport> answer;
    public List<DatabaseConnection> Tested { get; } = new();

    public FakeTester(Func<DatabaseConnection, DiagnosticReport> answer) => this.answer = answer;

    public Task<DiagnosticReport> RunAsync(DatabaseConnection connection, CancellationToken cancellationToken = default)
    {
        Tested.Add(connection);
        return Task.FromResult(answer(connection));
    }
}

/// <summary>The connection dialog reads a pasted string (A), warns while typing (B) and explains a failed test (C).</summary>
public class SmartConnectionDialogTests
{
    private static async Task<(Window Owner, FormDialog Dialog, Task<DatabaseConnection?> Result)> Open(DatabaseConnection? existing = null)
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

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) { Pump(); await Task.Delay(10); }
        Assert.True(condition(), "timed out waiting for the dialog");
    }

    private static T Named<T>(FormDialog d, string name) where T : Control => d.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);
    private static TextBox Url(FormDialog d) => Named<TextBox>(d, "ConnectionUrl");
    private static TextBlock UrlNote(FormDialog d) => Named<TextBlock>(d, "ConnectionUrlNote");
    private static TextBox Host(FormDialog d) => Named<TextBox>(d, "ConnectionHost");
    private static TextBox Secret(FormDialog d) => Named<TextBox>(d, "ConnectionSecret");
    private static StackPanel Advice(FormDialog d) => Named<StackPanel>(d, "ConnectionAdvice");
    private static StackPanel TestResult(FormDialog d) => Named<StackPanel>(d, "ConnectionTestResult");
    private static TextBox Database(FormDialog d) => d.GetLogicalDescendants().OfType<TextBox>().Single(t => t.Watermark == "Enter database name");
    private static TextBox User(FormDialog d) => d.GetLogicalDescendants().OfType<TextBox>().Single(t => t.Watermark == "Enter username");
    private static TextBox Name(FormDialog d) => d.GetLogicalDescendants().OfType<TextBox>().First();
    private static NumericUpDown Port(FormDialog d) => d.GetLogicalDescendants().OfType<NumericUpDown>().Single();
    private static ToggleSwitch Ssl(FormDialog d) => d.GetLogicalDescendants().OfType<ToggleSwitch>().Single();
    private static ComboBox ServerBox(FormDialog d) => d.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Items.Cast<object>().Contains("CrateDB"));
    private static string ServerName(FormDialog d) => (string)ServerBox(d).SelectedItem!;

    private static string[] AdviceTexts(FormDialog d) =>
        Advice(d).GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();

    private static string[] TextsIn(Control c) => c.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();

    private static void Choose(FormDialog d, string server)
    {
        var box = ServerBox(d);
        box.SelectedIndex = box.Items.Cast<object>().ToList().IndexOf(server);
        Pump();
    }

    private static void Paste(FormDialog d, string text) { Url(d).Text = text; Pump(); }

    private static Button TestButton(FormDialog d) => d.GetLogicalDescendants().OfType<Button>()
        .Single(b => b.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == "Test connection"));

    private static async Task<DatabaseConnection?> Save(FormDialog d, Task<DatabaseConnection?> result)
    {
        var save = Assert.Single(d.GetLogicalDescendants().OfType<Button>(), b => b.IsDefault);
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return await result;
    }

    private static async Task RunTest(FormDialog d)
    {
        var button = TestButton(d);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitFor(() => button.IsEnabled && TestResult(d).IsVisible && !TextsIn(TestResult(d)).Contains("Testing the connection…"));
    }

    // ------------------------------------------------------------------ A: paste and go

    [AvaloniaFact]
    public async Task APastedTimescaleUrl_FillsTheWholeForm()
    {
        var (owner, dialog, result) = await Open();
        try
        {
            Paste(dialog, "postgresql://app:pw@db.example.com:5433/metrics?sslmode=require");

            Assert.Equal("TimescaleDB", ServerName(dialog));
            Assert.Equal("db.example.com", Host(dialog).Text);
            Assert.Equal(5433, (int)Port(dialog).Value!);
            Assert.Equal("app", User(dialog).Text);
            Assert.Equal("pw", Secret(dialog).Text);
            Assert.Equal("metrics", Database(dialog).Text);
            Assert.True(Ssl(dialog).IsChecked);
            Assert.True(UrlNote(dialog).IsVisible);
            Assert.Contains("Filled in from the connection string: TimescaleDB on db.example.com:5433", UrlNote(dialog).Text);
            Assert.True(string.IsNullOrEmpty(Url(dialog).Text), "the pasted text, which may hold a password, is cleared");

            var saved = await Save(dialog, result);
            Assert.Equal(DatabaseKind.TimescaleDb, saved!.Kind);
            Assert.Equal("db.example.com", saved.Host);
            Assert.Equal(5433, saved.Port);
            Assert.Equal("app", saved.Username);
            Assert.Equal("pw", saved.Password);
            Assert.Equal("metrics", saved.Database);
            Assert.True(saved.UseSsl);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task APastedCrateUrl_HidesTheDatabase_AndKeepsTheStartingUserWhenThereIsNone()
    {
        var (owner, dialog, _) = await Open();
        try
        {
            Paste(dialog, "crate://localhost:5434");
            Assert.Equal("CrateDB", ServerName(dialog));
            Assert.Equal(5434, (int)Port(dialog).Value!);
            Assert.Equal("crate", User(dialog).Text);
            Assert.False(Database(dialog).IsVisible);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task APastedInfluxDb3Url_PutsTheTokenInTheSecretBox()
    {
        var (owner, dialog, result) = await Open();
        try
        {
            Paste(dialog, "https://influx.example.com:8181/telemetry?token=abc123");
            Assert.Equal("InfluxDB 3.x", ServerName(dialog));
            Assert.Equal("abc123", Secret(dialog).Text);
            Assert.False(User(dialog).IsVisible);
            var saved = await Save(dialog, result);
            Assert.Equal("abc123", saved!.Token);
            Assert.Null(saved.Password);
            Assert.Equal("telemetry", saved.Database);
            Assert.True(saved.UseSsl);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task APastedInfluxDb1Url_KeepsItsUserAndPassword()
    {
        var (owner, dialog, _) = await Open();
        try
        {
            Paste(dialog, "http://u:p@influx.example.com:8086/db");
            Assert.Equal("InfluxDB 1.x", ServerName(dialog));
            Assert.Equal("u", User(dialog).Text);
            Assert.Equal("p", Secret(dialog).Text);
            Assert.Equal("db", Database(dialog).Text);
            Assert.False(Ssl(dialog).IsChecked);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task APaste_ThatIsNotAConnectionString_ChangesNothing_AndSaysSo()
    {
        var (owner, dialog, _) = await Open();
        try
        {
            Host(dialog).Text = "kept-host";
            Paste(dialog, "just some words");
            Assert.Equal("kept-host", Host(dialog).Text);
            Assert.True(UrlNote(dialog).IsVisible);
            Assert.Contains("not a connection string", UrlNote(dialog).Text);
            Assert.Equal("just some words", Url(dialog).Text);        // left for the person to correct

            Paste(dialog, "mysql://root@localhost/db");
            Assert.Contains("does not know that kind of connection string", UrlNote(dialog).Text);
            Assert.Equal("kept-host", Host(dialog).Text);

            Paste(dialog, "");
            Assert.False(UrlNote(dialog).IsVisible);
        }
        finally { owner.Close(); }
    }

    // ------------------------------------------------------------------ B: notes while typing

    [AvaloniaFact]
    public async Task ACleanForm_ShowsNoNotes()
    {
        var (owner, dialog, _) = await Open();
        try
        {
            Host(dialog).Text = "influx.example.com";
            Pump();
            Assert.False(Advice(dialog).IsVisible);
            Assert.Empty(AdviceTexts(dialog));
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task ASchemeInTheHost_IsWarnedAbout_AndFixedWhenTheBoxIsLeft()
    {
        var (owner, dialog, result) = await Open();
        try
        {
            Host(dialog).Text = "http://influx.example.com:8086/";
            Pump();
            Assert.True(Advice(dialog).IsVisible);
            Assert.Contains(AdviceTexts(dialog), t => t.Contains("Remove \"http://\""));

            Host(dialog).RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
            Pump();
            Assert.Equal("influx.example.com", Host(dialog).Text);
            Assert.Equal(8086, (int)Port(dialog).Value!);
            Assert.False(Advice(dialog).IsVisible);

            var saved = await Save(dialog, result);
            Assert.Equal("influx.example.com", saved!.Host);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task AHostWithAPort_BecomesAHostAndAPort()
    {
        var (owner, dialog, result) = await Open();
        try
        {
            Choose(dialog, "TimescaleDB");
            Host(dialog).Text = "db.example.com:5433";
            Pump();
            Assert.Contains(AdviceTexts(dialog), t => t.Contains("port in the Port box"));
            Host(dialog).RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
            Pump();
            Assert.Equal("db.example.com", Host(dialog).Text);
            Assert.Equal(5433, (int)Port(dialog).Value!);
            Assert.Equal(5433, (await Save(dialog, result))!.Port);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task Saving_CleansTheHost_EvenIfTheBoxWasNeverLeft()
    {
        var (owner, dialog, result) = await Open();
        try
        {
            Choose(dialog, "TimescaleDB");
            Host(dialog).Text = "postgresql://db.example.com:5433/";
            Pump();
            var saved = await Save(dialog, result);
            Assert.Equal("db.example.com", saved!.Host);
            Assert.Equal(5433, saved.Port);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task TheHttpPortOfCrateDb_IsAnError_ShownAsANote_AndBlocksSaving()
    {
        var (owner, dialog, _) = await Open();
        try
        {
            Choose(dialog, "CrateDB");
            Port(dialog).Value = 4200;
            Pump();
            Assert.Contains(AdviceTexts(dialog), t => t.Contains("HTTP port"));
            Assert.Contains("HTTP port", dialog.Validate!());
            Port(dialog).Value = 5432;
            Pump();
            Assert.Null(dialog.Validate!());
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task InfluxDb3WithoutAToken_IsWarnedAbout_ButCanBeSaved()
    {
        var (owner, dialog, _) = await Open();
        try
        {
            Choose(dialog, "InfluxDB 3.x");
            Assert.Contains(AdviceTexts(dialog), t => t.Contains("needs an API token"));
            Assert.Null(dialog.Validate!());
            Secret(dialog).Text = "token";
            Pump();
            Assert.DoesNotContain(AdviceTexts(dialog), t => t.Contains("needs an API token"));
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task TheName_IsSuggestedFromTheUserAndServer_WhileItIsTheDefault()
    {
        var (owner, dialog, _) = await Open();
        try
        {
            Assert.Equal("localhost", Name(dialog).Text);
            Choose(dialog, "CrateDB");
            Assert.Equal("crate@localhost", Name(dialog).Text);
            Host(dialog).Text = "crate.example.com";
            Pump();
            Assert.Equal("crate@crate.example.com", Name(dialog).Text);
            Port(dialog).Value = 5434;
            Pump();
            Assert.Equal("crate@crate.example.com:5434", Name(dialog).Text);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task ANameThePersonTyped_IsNeverReplaced()
    {
        var (owner, dialog, _) = await Open();
        try
        {
            Name(dialog).Text = "Production metrics";
            Choose(dialog, "CrateDB");
            Host(dialog).Text = "other";
            Pump();
            Assert.Equal("Production metrics", Name(dialog).Text);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task ASavedConnectionsName_IsNeverReplaced()
    {
        var existing = new DatabaseConnection("c", "Prod crate", "crate.example.com", 5432, "app", "pw") { Kind = DatabaseKind.CrateDb };
        var (owner, dialog, _) = await Open(existing);
        try
        {
            Host(dialog).Text = "changed";
            Pump();
            Assert.Equal("Prod crate", Name(dialog).Text);
        }
        finally { owner.Close(); }
    }

    // ------------------------------------------------------------------ C: the diagnosing test

    private static DiagnosticReport Passing(IReadOnlyList<string>? databases = null, string version = "CrateDB 5.9.2") => new(
        new[]
        {
            new DiagnosticStep("Host found", StepStatus.Ok, "localhost", TimeSpan.FromMilliseconds(4)),
            new DiagnosticStep("Port open", StepStatus.Ok, "localhost:5434", TimeSpan.FromMilliseconds(2)),
            new DiagnosticStep("Sign in", StepStatus.Ok, "Found databases", TimeSpan.FromMilliseconds(30)),
        }, null, databases ?? new[] { "crate" }, version, TimeSpan.FromMilliseconds(12));

    private static DiagnosticReport Failing() => new(
        new[]
        {
            new DiagnosticStep("Host found", StepStatus.Ok, "localhost", TimeSpan.FromMilliseconds(4)),
            new DiagnosticStep("Port open", StepStatus.Ok, "localhost:5434", TimeSpan.FromMilliseconds(2)),
            new DiagnosticStep("Sign in", StepStatus.Failed, "The password was rejected for \"crate\".", TimeSpan.FromMilliseconds(30)),
        }, "A new CrateDB accepts the user crate with no password.", Array.Empty<string>(), null, TimeSpan.Zero);

    private static async Task<IDisposable> UseTester(IConnectionTester tester)
    {
        var previous = TickbirdDialogs.DiagnosticsFactory;
        TickbirdDialogs.DiagnosticsFactory = () => tester;
        await Task.CompletedTask;
        return new Restore(() => TickbirdDialogs.DiagnosticsFactory = previous);
    }

    private sealed class Restore : IDisposable
    {
        private readonly Action action;
        public Restore(Action action) => this.action = action;
        public void Dispose() => action();
    }

    [AvaloniaFact]
    public async Task AFailedTest_ShowsTheStepsTheFailureAndTheHint()
    {
        var tester = new FakeTester(_ => Failing());
        using var _ = await UseTester(tester);
        var (owner, dialog, _) = await Open();
        try
        {
            Choose(dialog, "CrateDB");
            await RunTest(dialog);
            var texts = TextsIn(TestResult(dialog));
            Assert.Contains("✘", texts);
            Assert.Contains("✔", texts);
            Assert.Contains(texts, t => t.Contains("The password was rejected for \"crate\"."));
            Assert.Contains(texts, t => t.Contains("A new CrateDB accepts the user crate with no password."));
            Assert.Empty(TestResult(dialog).GetLogicalDescendants().OfType<Button>());          // no Details link: the steps are already shown
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task APassingTest_IsOneLine_WithDetailsOnDemand()
    {
        var tester = new FakeTester(_ => Passing());
        using var _ = await UseTester(tester);
        var (owner, dialog, _) = await Open();
        try
        {
            Choose(dialog, "CrateDB");
            await RunTest(dialog);
            Assert.Contains(TextsIn(TestResult(dialog)), t => t.Contains("Connected to CrateDB 5.9.2 in 12 ms."));
            var steps = Named<StackPanel>(dialog, "ConnectionTestSteps");
            Assert.False(steps.IsVisible);
            var details = Named<Button>(dialog, "ConnectionTestDetails");
            details.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(steps.IsVisible);
            Assert.Equal("Hide details", details.Content);
            details.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(steps.IsVisible);
            Assert.Empty(TestResult(dialog).GetLogicalDescendants().OfType<ComboBox>());        // CrateDB has no database to pick
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task APassingTest_OffersTheDatabasesFound_AndFillsTheField()
    {
        var tester = new FakeTester(_ => Passing(new[] { "metrics", "logs", "postgres" }, "2.31.0 on PostgreSQL 16.3"));
        using var _ = await UseTester(tester);
        var (owner, dialog, _) = await Open();
        try
        {
            Choose(dialog, "TimescaleDB");
            await RunTest(dialog);
            var picker = Named<ComboBox>(dialog, "ConnectionTestDatabases");
            Assert.Equal(new[] { "(all databases)", "metrics", "logs", "postgres" }, picker.Items.Cast<string>());
            Assert.Equal(0, picker.SelectedIndex);

            picker.SelectedIndex = 2;
            Pump();
            Assert.Equal("logs", Database(dialog).Text);
            picker.SelectedIndex = 0;
            Pump();
            Assert.Equal("", Database(dialog).Text);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task TheDatabaseAlreadyTyped_IsPreselected()
    {
        var tester = new FakeTester(_ => Passing(new[] { "metrics", "logs" }));
        using var _ = await UseTester(tester);
        var (owner, dialog, _) = await Open();
        try
        {
            Choose(dialog, "TimescaleDB");
            Database(dialog).Text = "logs";
            await RunTest(dialog);
            Assert.Equal(2, Named<ComboBox>(dialog, "ConnectionTestDatabases").SelectedIndex);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task TheTest_UsesTheCleanedConnection_AsTyped()
    {
        var tester = new FakeTester(_ => Passing());
        using var _ = await UseTester(tester);
        var (owner, dialog, _) = await Open();
        try
        {
            Choose(dialog, "TimescaleDB");
            Host(dialog).Text = "postgresql://db.example.com:5433/";
            User(dialog).Text = "app";
            Pump();
            await RunTest(dialog);
            var tested = Assert.Single(tester.Tested);
            Assert.Equal("db.example.com", tested.Host);
            Assert.Equal(5433, tested.Port);
            Assert.Equal("app", tested.Username);
            Assert.Equal(DatabaseKind.TimescaleDb, tested.Kind);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task AProblemInTheForm_IsShownWithoutTestingAnything()
    {
        var tester = new FakeTester(_ => Passing());
        using var _ = await UseTester(tester);
        var (owner, dialog, _) = await Open();
        try
        {
            Choose(dialog, "CrateDB");
            Port(dialog).Value = 4200;
            Pump();
            TestButton(dialog).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            Assert.Empty(tester.Tested);
            Assert.True(TestResult(dialog).IsVisible);
            Assert.Contains(TextsIn(Named<Border>(dialog, "ConnectionTestFailure")), t => t.Contains("HTTP port"));
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task ATestThatThrows_IsShownAsAProblem_AndTheButtonComesBack()
    {
        var tester = new FakeTester(_ => throw new InvalidOperationException("the probe broke"));
        using var _ = await UseTester(tester);
        var (owner, dialog, _) = await Open();
        try
        {
            var button = TestButton(dialog);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => button.IsEnabled && TestResult(dialog).GetLogicalDescendants().OfType<Border>().Any(b => b.Name == "ConnectionTestFailure"));
            Assert.Contains(TextsIn(Named<Border>(dialog, "ConnectionTestFailure")), t => t.Contains("the probe broke"));
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task TheTestResult_IsHiddenUntilATestIsRun()
    {
        var (owner, dialog, _) = await Open();
        try { Assert.False(TestResult(dialog).IsVisible); }
        finally { owner.Close(); }
    }
}
