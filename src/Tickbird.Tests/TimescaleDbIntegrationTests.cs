using Tickbird.Core.Data;
using Tickbird.Core.Providers;

namespace Tickbird.Tests;

/// <summary>
/// Runs the TimescaleDB client against a real server. It does nothing unless TICKBIRD_TIMESCALE_HOST is set, so the
/// normal test run needs no database. To run it:
/// <code>
/// docker run -d --name tickbird-ts -p 5432:5432 -e POSTGRES_PASSWORD=secret timescale/timescaledb:latest-pg16
/// set TICKBIRD_TIMESCALE_HOST=localhost
/// set TICKBIRD_TIMESCALE_PASSWORD=secret
/// dotnet test src/Tickbird.Tests --filter TimescaleDbIntegrationTests
/// </code>
/// Optional: TICKBIRD_TIMESCALE_PORT (5432) and TICKBIRD_TIMESCALE_USER (postgres). It creates and drops its own database.
/// </summary>
public class TimescaleDbIntegrationTests
{
    private static DatabaseClient? Connect()
    {
        var host = Environment.GetEnvironmentVariable("TICKBIRD_TIMESCALE_HOST");
        if (string.IsNullOrEmpty(host)) return null;
        var port = ushort.Parse(Environment.GetEnvironmentVariable("TICKBIRD_TIMESCALE_PORT") ?? "5432");
        var user = Environment.GetEnvironmentVariable("TICKBIRD_TIMESCALE_USER") ?? "postgres";
        var password = Environment.GetEnvironmentVariable("TICKBIRD_TIMESCALE_PASSWORD") ?? "";
        return DatabaseClientFactory.Create(new DatabaseConnection("it", "integration", host, port, user, password, false, null)
        {
            Kind = DatabaseKind.TimescaleDb
        });
    }

    [Fact]
    public async Task EverythingTheUiDoes_WorksOnARealServer()
    {
        var client = Connect();
        if (client == null) return;

        var ping = await client.PingAsync();
        Assert.True(ping.Success, "Could not connect: " + ping.Version);
        Assert.Contains("TimescaleDB", client.Connection.Profile.DisplayName);

        var database = "tickbird_it_" + Guid.NewGuid().ToString("N")[..8];
        var user = "tickbird_it_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            // databases and tables
            var created = await client.CreateDatabaseAsync(database);
            Assert.True(created.Success, created.Body);
            Assert.Contains(database, await client.GetDatabaseNamesAsync());

            var setup = await client.QueryAsync(database,
                "CREATE TABLE cpu (time timestamptz NOT NULL, host text, region text, usage double precision);" +
                "SELECT create_hypertable('cpu', 'time')");
            Assert.NotNull(setup);
            Assert.Contains("cpu", await client.GetMeasurementNamesAsync(database));

            // writing and querying
            var start = DateTime.UtcNow.AddHours(-2);
            var points = Enumerable.Range(0, 120).Select(i => new DataPoint("cpu",
                new Dictionary<string, object> { ["host"] = i % 2 == 0 ? "web-1" : "web-2", ["region"] = "eu" },
                new Dictionary<string, object> { ["usage"] = 20.0 + i % 40 }, start.AddMinutes(i))).ToList();
            var written = await client.WriteAsync(database, points);
            Assert.True(written.Success, written.Body);
            var rows = (await client.QueryAsync(database, "SELECT count(*) AS n FROM cpu")).Single();
            Assert.Equal(120L, Convert.ToInt64(rows.Values[0][0]));

            // columns as tags and fields, tag values, chunks
            Assert.Equal(new[] { "host", "region" }, (await client.GetTagKeysAsync(database, "cpu")).OrderBy(t => t));
            Assert.Contains(await client.GetFieldKeysAsync(database, "cpu"), f => f.Name == "usage" && f.Type == "double precision");
            Assert.Equal(new[] { "web-1", "web-2" }, (await client.GetTagValuesAsync(database, "cpu", "host")).Select(v => v.Value));
            Assert.NotEmpty(await client.GetSeriesNamesAsync(database, "cpu"));
            Assert.Contains(await client.GetDatabaseSchemaAsync(database), s => s.Name == "cpu" && s.Tags.Contains("host"));

            // retention policy
            var policy = await client.CreateRetentionPolicyAsync(database, "cpu", "30d", 1);
            Assert.True(policy.Success, policy.Body);
            Assert.Contains(await client.GetRetentionPoliciesAsync(database), p => p.Name == "cpu" && p.Duration.Contains("30"));
            Assert.True((await client.DropRetentionPolicyAsync(database, "cpu")).Success);
            Assert.DoesNotContain(await client.GetRetentionPoliciesAsync(database), p => p.Name == "cpu");

            // continuous aggregate (continuous query) and backfill
            var aggregate = await client.CreateContinuousQueryAsync(new ContinuousQueryParams
            {
                Name = "cpu_1h", Database = database, Source = "cpu", Interval = "1h", Tags = new[] { "host" },
                SubQueries = new[] { "avg(usage) AS usage" }
            });
            Assert.True(aggregate.Success, aggregate.Body);
            Assert.Contains(await client.GetContinousQueriesAsync(database), q => q.Name == "cpu_1h");
            Assert.True((await client.DropContinuousQueryAsync(database, "cpu_1h")).Success);

            var backfill = await client.BackfillAsync(database, new BackfillParams
            {
                Source = "cpu", Destination = "cpu_10m", Interval = "10m", Tags = new[] { "host" },
                SubQueries = new[] { "avg(usage) AS usage" }, FromTime = start.AddHours(-1), ToTime = DateTime.UtcNow.AddHours(1)
            });
            Assert.True(backfill.Success, backfill.Body);
            Assert.True(Convert.ToInt64((await client.QueryAsync(database, "SELECT count(*) FROM cpu_10m")).Single().Values[0][0]) > 0);

            // users and privileges
            Assert.True((await client.CreateUserAsync(user, "p@ss'word", false)).Success);
            Assert.Contains(await client.GetUsersAsync(), u => u.Name == user && !u.IsAdmin);
            Assert.True((await client.GrantPrivilegeAsync(user, DatabasePrivileges.Read, database)).Success);
            Assert.Contains(await client.GetPrivilegesAsync(user), g => g.Database == database && g.Privilege == DatabasePrivileges.Read);
            Assert.True((await client.SetPasswordAsync(user, "another")).Success);

            // server information
            Assert.NotEmpty((await client.GetDiagnosticsAsync()).Properties);
            Assert.True((await client.GetStatsAsync()).Custom.ContainsKey("Databases"));
            _ = await client.GetRunningQueriesAsync();

            // truncating and dropping
            Assert.True((await client.DropSeriesAsync(database, "cpu")).Success);
            Assert.Equal(0L, Convert.ToInt64((await client.QueryAsync(database, "SELECT count(*) FROM cpu")).Single().Values[0][0]));
            Assert.True((await client.DropMeasurementAsync(database, "cpu_10m")).Success);
        }
        finally
        {
            await client.DropDatabaseAsync(database);     // first: the user's privileges live in this database
            await client.DropUserAsync(user);
        }
    }
}
