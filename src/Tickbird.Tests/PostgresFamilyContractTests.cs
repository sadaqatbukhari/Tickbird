using Tickbird.Core.Data;
using Tickbird.Core.Data.Sql;
using Tickbird.Core.Providers;

namespace Tickbird.Tests;

/// <summary>
/// What every database of the PostgreSQL family must honour, run against each of them. A new database that derives from
/// <see cref="PostgresClientBase"/> joins <see cref="Families"/> and gets all of this for free: whatever its profile says it
/// can do must work, and whatever it says it cannot do must be refused clearly and never reach the server.
/// </summary>
public class PostgresFamilyContractTests
{
    public static IEnumerable<object[]> Families() => new[]
    {
        new object[] { DatabaseKind.TimescaleDb },
        new object[] { DatabaseKind.CrateDb },
    };

    private static (DatabaseClient Client, FakeSqlRunner Runner) Create(DatabaseKind kind)
    {
        var runner = new FakeSqlRunner();
        var connection = new DatabaseConnection("1", "x", "localhost", 5432, "u", "p") { Kind = kind };
        DatabaseClient client = kind switch
        {
            DatabaseKind.TimescaleDb => new TimescaleDbClient(connection, runner),
            DatabaseKind.CrateDb => new CrateDbClient(connection, runner),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return (client, runner);
    }

    private static BackfillParams Backfill() => new()
    {
        Source = "cpu", Destination = "cpu_1h", Interval = "1h", FromTime = DateTime.UtcNow.AddDays(-1), ToTime = DateTime.UtcNow,
        SubQueries = new[] { "avg(\"v\") AS \"v\"" }, Tags = new[] { "host" }
    };

    /// <summary>Each capability with one call that needs it.</summary>
    private static readonly (DatabaseCapabilities Capability, Func<DatabaseClient, Task> Call)[] Operations =
    {
        (DatabaseCapabilities.CreateDatabase, c => c.CreateDatabaseAsync("d")),
        (DatabaseCapabilities.DropDatabase, c => c.DropDatabaseAsync("d")),
        (DatabaseCapabilities.Users, c => c.GetUsersAsync()),
        (DatabaseCapabilities.Users, c => c.CreateUserAsync("u", "p", false)),
        (DatabaseCapabilities.Users, c => c.DropUserAsync("u")),
        (DatabaseCapabilities.Users, c => c.SetPasswordAsync("u", "p")),
        (DatabaseCapabilities.AdministratorRights, c => c.GrantAdministratorAsync("u")),
        (DatabaseCapabilities.AdministratorRights, c => c.RevokeAdministratorAsync("u")),
        (DatabaseCapabilities.Privileges, c => c.GetPrivilegesAsync("u")),
        (DatabaseCapabilities.Privileges, c => c.GrantPrivilegeAsync("u", DatabasePrivileges.Read, "d")),
        (DatabaseCapabilities.Privileges, c => c.RevokePrivilegeAsync("u", DatabasePrivileges.Read, "d")),
        (DatabaseCapabilities.RetentionPolicies, c => c.GetRetentionPoliciesAsync("d")),
        (DatabaseCapabilities.RetentionPolicies, c => c.CreateRetentionPolicyAsync("d", "cpu", "7d", 1)),
        (DatabaseCapabilities.RetentionPolicies, c => c.AlterRetentionPolicyAsync("d", "cpu", "7d", 1)),
        (DatabaseCapabilities.RetentionPolicies, c => c.DropRetentionPolicyAsync("d", "cpu")),
        (DatabaseCapabilities.ContinuousQueries, c => c.GetContinousQueriesAsync("d")),
        (DatabaseCapabilities.ContinuousQueries, c => c.DropContinuousQueryAsync("d", "q")),
        (DatabaseCapabilities.Backfill, c => c.BackfillAsync("d", Backfill())),
        (DatabaseCapabilities.RunningQueries, c => c.GetRunningQueriesAsync()),
        (DatabaseCapabilities.Statistics, c => c.GetStatsAsync()),
        (DatabaseCapabilities.Diagnostics, c => c.GetDiagnosticsAsync()),
        (DatabaseCapabilities.TagKeys, c => c.GetTagKeysAsync("d", "cpu")),
        (DatabaseCapabilities.TagValues, c => c.GetTagValuesAsync("d", "cpu", "host")),
        (DatabaseCapabilities.FieldKeys, c => c.GetFieldKeysAsync("d", "cpu")),
        (DatabaseCapabilities.Series, c => c.GetSeriesNamesAsync("d", "cpu")),
        (DatabaseCapabilities.DropSeries, c => c.DropSeriesAsync("d", "cpu")),
        (DatabaseCapabilities.DropMeasurement, c => c.DropMeasurementAsync("d", "cpu")),
        (DatabaseCapabilities.WriteData, c => c.WriteAsync("d", "cpu", new Dictionary<string, object>(), new Dictionary<string, object> { ["v"] = 1.0 }, DateTime.UtcNow)),
    };

    [Theory]
    [MemberData(nameof(Families))]
    public async Task WhatTheProfileSupports_DoesNotThrowNotSupported(DatabaseKind kind)
    {
        var (client, _) = Create(kind);
        var profile = DatabaseProfiles.Get(kind);
        foreach (var (capability, call) in Operations.Where(o => profile.Supports(o.Capability)))
        {
            var error = await Record.ExceptionAsync(() => call(client));
            Assert.False(error is NotSupportedException, $"{kind}: {capability} is declared but not implemented");
        }
    }

    [Theory]
    [MemberData(nameof(Families))]
    public async Task WhatTheProfileDoesNotSupport_IsRefused_AndNeverReachesTheServer(DatabaseKind kind)
    {
        var (client, runner) = Create(kind);
        var profile = DatabaseProfiles.Get(kind);
        foreach (var (capability, call) in Operations.Where(o => !profile.Supports(o.Capability)))
        {
            runner.Calls.Clear();
            var error = await Record.ExceptionAsync(() => call(client));
            Assert.True(error is NotSupportedException, $"{kind}: {capability} is not declared, so it must say it is not supported");
            Assert.Empty(runner.Calls);
        }
    }

    [Theory]
    [MemberData(nameof(Families))]
    public async Task TheTreeIsFilled_FromTheDatabaseGiven(DatabaseKind kind)
    {
        var (client, runner) = Create(kind);
        runner.Answer("table_name", new[] { "schema_name", "table_name" }, new object[] { "app", "events" }, new object[] { "other", "cpu" });
        runner.Answer("relname", new[] { "schema_name", "table_name" }, new object[] { "app", "events" }, new object[] { "other", "cpu" });
        var names = (await client.GetMeasurementNamesAsync("d")).ToList();
        Assert.Equal(new[] { "app.events", "other.cpu" }, names);       // neither "app" nor "other" is a default schema
        Assert.Equal("d", runner.Queried.Single().Database);
    }

    [Theory]
    [MemberData(nameof(Families))]
    public async Task ErrorsFromTheServer_BecomeFailedResults(DatabaseKind kind)
    {
        var connection = new DatabaseConnection("1", "x", "h", 5432, "u", "p") { Kind = kind };
        var failing = new FailingRunner(1, new Npgsql.PostgresException("nope", "ERROR", "ERROR", "42501"));
        DatabaseClient client = kind == DatabaseKind.CrateDb ? new CrateDbClient(connection, failing) : new TimescaleDbClient(connection, failing);
        var result = await client.DropUserAsync("u");
        Assert.False(result.Success);
        Assert.Equal("nope", result.Body);
    }

    [Theory]
    [MemberData(nameof(Families))]
    public async Task ALostConnection_IsAFailedResult(DatabaseKind kind)
    {
        var connection = new DatabaseConnection("1", "x", "h", 5432, "u", "p") { Kind = kind };
        var failing = new FailingRunner(1, new Npgsql.NpgsqlException("lost", new TimeoutException("timed out")));
        DatabaseClient client = kind == DatabaseKind.CrateDb ? new CrateDbClient(connection, failing) : new TimescaleDbClient(connection, failing);
        var result = await client.DropUserAsync("u");
        Assert.False(result.Success);
        Assert.Equal("timed out", result.Body);
    }

    [Theory]
    [MemberData(nameof(Families))]
    public async Task AWrite_SendsValuesAsParameters_NeverInTheSql(DatabaseKind kind)
    {
        var (client, runner) = Create(kind);
        await client.WriteAsync("d", "cpu", new Dictionary<string, object> { ["host"] = "x'); DROP TABLE cpu; --" },
            new Dictionary<string, object> { ["v"] = 1.5 }, DateTime.UtcNow);
        var call = runner.Executed.Single();
        Assert.DoesNotContain("DROP TABLE", call.Sql);
        Assert.Contains(call.Parameters!.Values, v => v is UntypedText { Value: var text } && text.Contains("DROP TABLE"));
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void EveryProfile_IsCompleteEnoughForTheUi(DatabaseKind kind)
    {
        var profile = DatabaseProfiles.Get(kind);
        Assert.False(string.IsNullOrWhiteSpace(profile.DisplayName));
        Assert.InRange((int)profile.DefaultPort, 1, 65535);
        Assert.True(profile.IsSql);
        Assert.False(string.IsNullOrWhiteSpace(profile.SampleQuery("cpu")));
        Assert.Contains("cpu", profile.SampleQuery("cpu"));
        foreach (var word in new[]
        {
            profile.Terms.Measurement, profile.Terms.Measurements, profile.Terms.TagKeys, profile.Terms.TagValues, profile.Terms.FieldKeys,
            profile.Terms.Series, profile.Terms.DropSeries, profile.Terms.Users, profile.Terms.Database, profile.Terms.Databases
        }) Assert.False(string.IsNullOrWhiteSpace(word));
        Assert.IsAssignableFrom<PostgresClientBase>(profile.CreateClient(new DatabaseConnection("1", "x", "h", profile.DefaultPort, "u", "p") { Kind = kind }));
    }
}
