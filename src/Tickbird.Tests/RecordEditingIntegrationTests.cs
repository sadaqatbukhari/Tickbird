using Tickbird.Core.Data;
using Tickbird.Core.Providers;

namespace Tickbird.Tests;

/// <summary>
/// Inserts, changes and deletes records on real servers. Does nothing unless TICKBIRD_TIMESCALE_HOST (with _PORT and _PASSWORD)
/// or TICKBIRD_CRATE_HOST (with _PORT) is set, like the other integration tests. It creates and drops its own tables, named tickbird_it_*.
/// </summary>
public class RecordEditingIntegrationTests
{
    private static (DatabaseClient Client, string Database, string Schema)? Timescale()
    {
        var host = Environment.GetEnvironmentVariable("TICKBIRD_TIMESCALE_HOST");
        if (string.IsNullOrEmpty(host)) return null;
        var connection = new DatabaseConnection("t", "t", host, ushort.Parse(Environment.GetEnvironmentVariable("TICKBIRD_TIMESCALE_PORT") ?? "5432"), "postgres",
            Environment.GetEnvironmentVariable("TICKBIRD_TIMESCALE_PASSWORD") ?? "", false, "postgres") { Kind = DatabaseKind.TimescaleDb };
        return (DatabaseClientFactory.Create(connection), "postgres", "public");
    }

    private static (DatabaseClient Client, string Database, string Schema)? Crate()
    {
        var host = Environment.GetEnvironmentVariable("TICKBIRD_CRATE_HOST");
        if (string.IsNullOrEmpty(host)) return null;
        var connection = new DatabaseConnection("c", "c", host, ushort.Parse(Environment.GetEnvironmentVariable("TICKBIRD_CRATE_PORT") ?? "5432"), "crate", "", false) { Kind = DatabaseKind.CrateDb };
        return (DatabaseClientFactory.Create(connection), "crate", "doc");
    }

    private static async Task<List<IList<object>>> Select(DatabaseClient client, string database, string sql) =>
        ((await client.QueryAsync(database, sql)).FirstOrDefault()?.Values ?? new List<IList<object>>()).ToList();

    [Fact]
    public async Task TimescaleDb_RecordsCanBeInsertedChangedAndDeleted()
    {
        if (Timescale() is not { } t) return;
        await Run(t.Client, t.Database, "timestamptz", "double precision");
    }

    [Fact]
    public async Task CrateDb_RecordsCanBeInsertedChangedAndDeleted()
    {
        if (Crate() is not { } c) return;
        await Run(c.Client, c.Database, "timestamp with time zone", "double precision");
    }

    private static async Task Run(DatabaseClient client, string database, string timeType, string doubleType)
    {
        var keyed = "tickbird_it_" + Guid.NewGuid().ToString("N")[..8];
        var keyless = keyed + "_nokey";
        var q = database == "crate" ? "doc." : "";   // CrateDB reads the connection database as the default schema
        try
        {
            await client.QueryAsync(database, $"CREATE TABLE {q}{keyed} (id integer PRIMARY KEY, host text, usage {doubleType}, seen {timeType})");
            await client.QueryAsync(database, $"CREATE TABLE {q}{keyless} (host text, usage {doubleType})");

            Assert.Contains(keyed, await client.GetMeasurementNamesAsync(database));
            // with a primary key: the key identifies the record
            var schema = await client.GetRecordSchemaAsync(database, keyed);
            Assert.True(schema.KeyIsExact, "primary key not found");
            Assert.Equal(new[] { "id" }, schema.Identity.Select(c => c.Name));

            var inserted = await client.InsertRecordAsync(database, schema, new Dictionary<string, object>
                { ["id"] = "1", ["host"] = "web-1", ["usage"] = "1.5", ["seen"] = "2024-05-01T12:30:00Z" });
            Assert.True(inserted.Success, inserted.Body);
            Assert.True((await client.InsertRecordAsync(database, schema, new Dictionary<string, object> { ["id"] = "2", ["host"] = "web-2" })).Success);

            var rows = await Select(client, database, $"SELECT id, host, usage, seen FROM {q}{keyed} ORDER BY id");
            Assert.Equal(2, rows.Count);

            var original = new Dictionary<string, object> { ["id"] = Convert.ToInt64(rows[0][0]), ["host"] = rows[0][1], ["usage"] = rows[0][2], ["seen"] = rows[0][3] };
            var changed = await client.UpdateRecordAsync(database, schema, original,
                new Dictionary<string, object> { ["host"] = "web-9", ["usage"] = "2.5" });
            Assert.True(changed.Success, changed.Body);
            rows = await Select(client, database, $"SELECT id, host, usage FROM {q}{keyed} ORDER BY id");
            Assert.Equal("web-9", rows[0][1]);
            Assert.Equal(2.5, Convert.ToDouble(rows[0][2], System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal("web-2", rows[1][1]);     // the other record is untouched

            // setting a value to NULL
            var cleared = await client.UpdateRecordAsync(database, schema, new Dictionary<string, object> { ["id"] = 1L }, new Dictionary<string, object> { ["host"] = null! });
            Assert.True(cleared.Success, cleared.Body);
            Assert.Null((await Select(client, database, $"SELECT host FROM {q}{keyed} WHERE id = 1"))[0][0]);

            // a record that is gone
            var stale = await client.DeleteRecordAsync(database, schema, new Dictionary<string, object> { ["id"] = 99L });
            Assert.False(stale.Success);
            Assert.Contains("not found", stale.Body);

            var deleted = await client.DeleteRecordAsync(database, schema, new Dictionary<string, object> { ["id"] = 1L });
            Assert.True(deleted.Success, deleted.Body);
            Assert.Single(await Select(client, database, $"SELECT id FROM {q}{keyed}"));

            // without a primary key: all values identify the record, and identical rows are refused
            var plain = await client.GetRecordSchemaAsync(database, keyless);
            Assert.False(plain.KeyIsExact);
            await client.InsertRecordAsync(database, plain, new Dictionary<string, object> { ["host"] = "a", ["usage"] = "1" });
            await client.InsertRecordAsync(database, plain, new Dictionary<string, object> { ["host"] = "a", ["usage"] = "1" });
            await client.InsertRecordAsync(database, plain, new Dictionary<string, object> { ["host"] = "b", ["usage"] = "2" });
            var duplicate = new Dictionary<string, object> { ["host"] = "a", ["usage"] = 1.0 };
            var refused = await client.DeleteRecordAsync(database, plain, duplicate);
            Assert.False(refused.Success);
            Assert.Contains("2 identical records", refused.Body);
            Assert.Equal(3, (await Select(client, database, $"SELECT host FROM {q}{keyless}")).Count);      // nothing was deleted

            var single = await client.DeleteRecordAsync(database, plain, new Dictionary<string, object> { ["host"] = "b", ["usage"] = 2.0 });
            Assert.True(single.Success, single.Body);
            Assert.Equal(2, (await Select(client, database, $"SELECT host FROM {q}{keyless}")).Count);

            // a value the column cannot hold is reported, not thrown
            var bad = await client.InsertRecordAsync(database, schema, new Dictionary<string, object> { ["id"] = "not a number" });
            Assert.False(bad.Success);
        }
        finally
        {
            foreach (var table in new[] { keyed, keyless })
                try { await client.QueryAsync(database, $"DROP TABLE IF EXISTS {q}{table}"); } catch { /* clean-up only */ }
        }
    }
}
