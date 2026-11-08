using Tickbird.Core.Data;
using Tickbird.Core.Providers;

namespace Tickbird.Tests;

/// <summary>
/// Runs the connection test against real servers. It does nothing unless TICKBIRD_TIMESCALE_HOST (with _PORT and _PASSWORD)
/// or TICKBIRD_CRATE_HOST (with _PORT) is set, the same variables as the other integration tests.
/// </summary>
public class ConnectionDiagnosticsIntegrationTests
{
    private static DatabaseConnection? Timescale(string? password = null, string? database = null, ushort? port = null, string user = "postgres")
    {
        var host = Environment.GetEnvironmentVariable("TICKBIRD_TIMESCALE_HOST");
        if (string.IsNullOrEmpty(host)) return null;
        return new DatabaseConnection("t", "t", host, port ?? ushort.Parse(Environment.GetEnvironmentVariable("TICKBIRD_TIMESCALE_PORT") ?? "5432"), user,
            password ?? Environment.GetEnvironmentVariable("TICKBIRD_TIMESCALE_PASSWORD") ?? "", false, database) { Kind = DatabaseKind.TimescaleDb };
    }

    private static DatabaseConnection? Crate(ushort? port = null)
    {
        var host = Environment.GetEnvironmentVariable("TICKBIRD_CRATE_HOST");
        if (string.IsNullOrEmpty(host)) return null;
        return new DatabaseConnection("c", "c", host, port ?? ushort.Parse(Environment.GetEnvironmentVariable("TICKBIRD_CRATE_PORT") ?? "5432"), "crate", "", false) { Kind = DatabaseKind.CrateDb };
    }

    [Fact]
    public async Task TimescaleDb_PassesEveryStep_AndListsItsDatabases()
    {
        var connection = Timescale();
        if (connection == null) return;
        var report = await new ConnectionDiagnostics().RunAsync(connection);
        Assert.True(report.Success, string.Join("; ", report.Steps.Select(s => $"{s.Name}: {s.Detail}")));
        Assert.Contains("postgres", report.Databases);
        Assert.Contains("PostgreSQL", report.Version ?? "");      // "2.31.0 on PostgreSQL 16.x": the extension version, then the server
    }

    [Fact]
    public async Task TimescaleDb_WrongPassword_NamesTheUser_AndGivesTheHint()
    {
        var connection = Timescale(password: "definitely-wrong");
        if (connection == null) return;
        var report = await new ConnectionDiagnostics().RunAsync(connection);
        Assert.Equal("Sign in", report.Failed!.Name);
        Assert.Equal("The password was rejected for \"postgres\".", report.Failed.Detail);
        Assert.Contains("POSTGRES_PASSWORD", report.Hint);
        Assert.Equal(new[] { StepStatus.Ok, StepStatus.Ok, StepStatus.Failed }, report.Steps.Select(s => s.Status));
    }

    [Fact]
    public async Task TimescaleDb_AMissingDatabase_IsExplained()
    {
        var connection = Timescale(database: "no_such_database_here");
        if (connection == null) return;
        var report = await new ConnectionDiagnostics().RunAsync(connection);
        Assert.Equal("The database \"no_such_database_here\" does not exist.", report.Failed!.Detail);
        Assert.Contains("Clear the Database field", report.Hint);
    }

    [Fact]
    public async Task TimescaleDb_ANamedDatabase_IsReadDirectly()
    {
        var connection = Timescale(database: "postgres");
        if (connection == null) return;
        var report = await new ConnectionDiagnostics().RunAsync(connection);
        Assert.True(report.Success, string.Join("; ", report.Steps.Select(s => $"{s.Name}: {s.Detail}")));
        Assert.Equal(new[] { "postgres" }, report.Databases);
    }

    [Fact]
    public async Task ARefusedPort_IsExplained_ForBothDatabases()
    {
        foreach (var connection in new[] { Timescale(port: 5999), Crate(port: 5999) })
        {
            if (connection == null) continue;
            var report = await new ConnectionDiagnostics().RunAsync(connection);
            Assert.Equal("Port open", report.Failed!.Name);
            Assert.Contains("Nothing is listening on", report.Failed.Detail);
            Assert.Contains("by default", report.Hint);
            Assert.Equal(StepStatus.Skipped, report.Steps[2].Status);
        }
    }

    [Fact]
    public async Task CrateDb_PassesEveryStep()
    {
        var connection = Crate();
        if (connection == null) return;
        var report = await new ConnectionDiagnostics().RunAsync(connection);
        Assert.True(report.Success, string.Join("; ", report.Steps.Select(s => $"{s.Name}: {s.Detail}")));
        Assert.Equal(new[] { "crate" }, report.Databases);
        Assert.StartsWith("CrateDB", report.Version);
    }
}
