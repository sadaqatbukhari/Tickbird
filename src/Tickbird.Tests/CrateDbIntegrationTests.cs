using Tickbird.Core.Data;
using Tickbird.Core.Providers;

namespace Tickbird.Tests;

/// <summary>
/// Runs the CrateDB client against a real server. It does nothing unless TICKBIRD_CRATE_HOST is set, so the normal test
/// run needs no database. To run it:
/// <code>
/// docker run -d --name tickbird-crate -p 5432:5432 -p 4200:4200 crate:latest -Cdiscovery.type=single-node
/// set TICKBIRD_CRATE_HOST=localhost
/// dotnet test src/Tickbird.Tests --filter CrateDbIntegrationTests
/// </code>
/// Optional: TICKBIRD_CRATE_PORT (5432), TICKBIRD_CRATE_USER (crate) and TICKBIRD_CRATE_PASSWORD (empty). It creates and
/// drops its own tables and user, all named tickbird_it_*.
/// </summary>
public class CrateDbIntegrationTests
{
    private static DatabaseClient? Connect()
    {
        var host = Environment.GetEnvironmentVariable("TICKBIRD_CRATE_HOST");
        if (string.IsNullOrEmpty(host)) return null;
        var port = ushort.Parse(Environment.GetEnvironmentVariable("TICKBIRD_CRATE_PORT") ?? "5432");
        var user = Environment.GetEnvironmentVariable("TICKBIRD_CRATE_USER") ?? "crate";
        var password = Environment.GetEnvironmentVariable("TICKBIRD_CRATE_PASSWORD") ?? "";
        return DatabaseClientFactory.Create(new DatabaseConnection("it", "integration", host, port, user, password, false, null)
        {
            Kind = DatabaseKind.CrateDb
        });
    }

    [Fact]
    public async Task EverythingTheUiDoes_WorksOnARealServer()
    {
        var client = Connect();
        if (client == null) return;

        var ping = await client.PingAsync();
        Assert.True(ping.Success, "Could not connect: " + ping.Version);
        Assert.StartsWith("CrateDB", ping.Version);
        Assert.Equal(new[] { "crate" }, await client.GetDatabaseNamesAsync());

        var table = "tickbird_it_" + Guid.NewGuid().ToString("N")[..8];
        var events = table + "_events";
        var user = "tickbird_it_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await client.QueryAsync("crate",
                $"CREATE TABLE doc.{table} (time timestamp with time zone, host text, region text, usage double precision)");
            await client.QueryAsync("crate",
                $"CREATE TABLE doc.{events} (time timestamp with time zone, host text, day timestamp with time zone GENERATED ALWAYS AS date_trunc('day', time)) PARTITIONED BY (day)");
            Assert.Contains(table, await client.GetMeasurementNamesAsync("crate"));

            // writing and querying
            var start = DateTime.UtcNow.AddHours(-2);
            var points = Enumerable.Range(0, 60).Select(i => new DataPoint(table,
                new Dictionary<string, object> { ["host"] = i % 2 == 0 ? "web-1" : "web-2", ["region"] = "eu" },
                new Dictionary<string, object> { ["usage"] = 20.0 + i % 40 }, start.AddMinutes(i))).ToList();
            var written = await client.WriteAsync("crate", points);
            Assert.True(written.Success, written.Body);
            await client.QueryAsync("crate", $"REFRESH TABLE doc.{table}");
            var rows = (await client.QueryAsync("crate", $"SELECT count(*) AS n FROM doc.{table}")).Single();
            Assert.Equal(60L, Convert.ToInt64(rows.Values[0][0]));

            // columns as tags and fields, tag values, partitions
            Assert.Equal(new[] { "host", "region" }, (await client.GetTagKeysAsync("crate", table)).OrderBy(t => t));
            Assert.Contains(await client.GetFieldKeysAsync("crate", table), f => f.Name == "usage");
            Assert.Equal(new[] { "web-1", "web-2" }, (await client.GetTagValuesAsync("crate", table, "host")).Select(v => v.Value));
            Assert.NotEmpty(await client.GetSeriesNamesAsync("crate", table));
            Assert.Contains(await client.GetDatabaseSchemaAsync("crate"), s => s.Name == table && s.Tags.Contains("host"));
            await client.WriteAsync("crate", events, new Dictionary<string, object> { ["host"] = "a" }, new Dictionary<string, object>(), start);
            await client.QueryAsync("crate", $"REFRESH TABLE doc.{events}");
            Assert.Contains(await client.GetSeriesNamesAsync("crate", events), s => s.Contains(",partition="));

            // users and privileges
            Assert.True((await client.CreateUserAsync(user, "p@ss'word\\1", false)).Success);
            Assert.Contains(await client.GetUsersAsync(), u => u.Name == user && !u.IsAdmin);
            Assert.True((await client.GrantPrivilegeAsync(user, DatabasePrivileges.Read, "crate")).Success);
            Assert.Contains(await client.GetPrivilegesAsync(user), g => g.Privilege == DatabasePrivileges.Read);
            Assert.True((await client.GrantPrivilegeAsync(user, DatabasePrivileges.All, "crate")).Success);
            Assert.Contains(await client.GetPrivilegesAsync(user), g => g.Privilege == DatabasePrivileges.All);
            Assert.True((await client.RevokePrivilegeAsync(user, DatabasePrivileges.All, "crate")).Success);
            Assert.Empty(await client.GetPrivilegesAsync(user));
            Assert.True((await client.SetPasswordAsync(user, "another")).Success);

            // server information
            Assert.NotEmpty((await client.GetDiagnosticsAsync()).Properties);
            var stats = await client.GetStatsAsync();
            Assert.True(stats.Custom.ContainsKey("Nodes"));
            _ = await client.GetRunningQueriesAsync();

            // deleting rows and dropping
            Assert.True((await client.DropSeriesAsync("crate", table)).Success);
            await client.QueryAsync("crate", $"REFRESH TABLE doc.{table}");
            Assert.Equal(0L, Convert.ToInt64((await client.QueryAsync("crate", $"SELECT count(*) FROM doc.{table}")).Single().Values[0][0]));
            Assert.True((await client.DropMeasurementAsync("crate", events)).Success);
        }
        finally
        {
            await client.DropMeasurementAsync("crate", table);
            await client.DropMeasurementAsync("crate", events);
            await client.DropUserAsync(user);
        }
    }
}
