using Tickbird.Core.Data;
using Tickbird.Core.Data.Sql;

namespace Tickbird.Tests;

/// <summary>A runner that records the SQL and answers from canned results, so no database is needed.</summary>
internal sealed class FakeSqlRunner : ISqlRunner
{
    public sealed record Call(string? Database, string Sql, IReadOnlyDictionary<string, object?>? Parameters, bool IsQuery);

    public List<Call> Calls { get; } = new();
    private readonly List<(string Contains, ResultSeries Result)> answers = new();

    /// <summary>Answers any query whose SQL contains <paramref name="contains"/> with a table.</summary>
    public FakeSqlRunner Answer(string contains, string[] columns, params object[][] rows)
    {
        answers.Add((contains, new ResultSeries("result", columns, new Dictionary<string, string>(),
            rows.Select(r => (IList<object>)r.ToList()).ToList())));
        return this;
    }

    public Task<IReadOnlyList<ResultSeries>> QueryAsync(string? database, string sql,
        IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        Calls.Add(new Call(database, sql, parameters, true));
        var match = answers.FirstOrDefault(a => sql.Contains(a.Contains, StringComparison.Ordinal));
        IReadOnlyList<ResultSeries> result = match.Result == null ? new List<ResultSeries>() : new List<ResultSeries> { match.Result };
        return Task.FromResult(result);
    }

    public Task<int> ExecuteAsync(string? database, string sql,
        IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        Calls.Add(new Call(database, sql, parameters, false));
        return Task.FromResult(0);
    }

    public IEnumerable<Call> Executed => Calls.Where(c => !c.IsQuery);
    public IEnumerable<Call> Queried => Calls.Where(c => c.IsQuery);
}

public class PostgresSqlTests
{
    [Fact]
    public void Identifiers_AreQuoted_AndQuotesDoubled()
    {
        Assert.Equal("\"cpu\"", PostgresSql.Identifier("cpu"));
        Assert.Equal("\"my \"\"odd\"\" name\"", PostgresSql.Identifier("my \"odd\" name"));
        Assert.Throws<ArgumentException>(() => PostgresSql.Identifier(""));
    }

    [Theory]
    [InlineData("cpu", "public", "cpu")]
    [InlineData("metrics.cpu", "metrics", "cpu")]
    [InlineData("\"my.schema\".\"my.table\"", "my.schema", "my.table")]
    [InlineData("\"a\"\"b\"", "public", "a\"b")]
    public void QualifiedNames_AreSplit(string name, string schema, string table) =>
        Assert.Equal((schema, table), PostgresSql.SplitQualified(name));

    [Fact]
    public void Tables_AreQualified_AndDisplayedWithoutThePublicSchema()
    {
        Assert.Equal("\"public\".\"cpu\"", PostgresSql.Table("cpu"));
        Assert.Equal("\"metrics\".\"cpu\"", PostgresSql.Table("metrics.cpu"));
        Assert.Equal("cpu", PostgresSql.DisplayName("public", "cpu"));
        Assert.Equal("metrics.cpu", PostgresSql.DisplayName("metrics", "cpu"));
    }

    [Fact]
    public void Literals_AreEscaped()
    {
        Assert.Equal("'o''neil'", PostgresSql.Literal("o'neil"));
        Assert.Equal("NULL", PostgresSql.Literal(null));
        Assert.Equal("E'a\\\\b'", PostgresSql.Literal("a\\b"));     // a backslash switches to the escape syntax
        // a quote inside the value cannot end the literal early
        Assert.Equal("'''; DROP TABLE x; --'", PostgresSql.Literal("'; DROP TABLE x; --"));
    }

    [Theory]
    [InlineData("30d", "30 days")]
    [InlineData("1w", "1 weeks")]
    [InlineData("12h", "12 hours")]
    [InlineData("90m", "90 minutes")]
    [InlineData("45s", "45 seconds")]
    [InlineData("500ms", "500 milliseconds")]
    [InlineData("2.5h", "2.5 hours")]
    [InlineData("30 days", "30 days")]
    [InlineData("1 mon", "1 mon")]
    public void Durations_BecomeIntervals(string input, string expected) => Assert.Equal(expected, PostgresSql.Interval(input));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("1d'; DROP TABLE x; --")]
    [InlineData("1d)")]
    public void BadDurations_AreRejected(string input) => Assert.Throws<FormatException>(() => PostgresSql.Interval(input));

    [Fact]
    public void IntervalLiteral_IsQuoted() => Assert.Equal("INTERVAL '30 days'", PostgresSql.IntervalLiteral("30d"));

    [Theory]
    [InlineData("text", true)]
    [InlineData("character varying(32)", true)]
    [InlineData("uuid", true)]
    [InlineData("double precision", false)]
    [InlineData("integer", false)]
    [InlineData("boolean", false)]
    [InlineData("timestamp with time zone", false)]
    public void TextColumns_AreTags(string type, bool isText) => Assert.Equal(isText, PostgresSql.IsTextType(type));
}

public class TimescaleDbClientTests
{
    private static (TimescaleDbClient Client, FakeSqlRunner Runner) Create(Action<FakeSqlRunner>? answers = null)
    {
        var runner = new FakeSqlRunner();
        answers?.Invoke(runner);
        var connection = new DatabaseConnection("1", "ts", "localhost", 5432, "app", "secret") { Kind = Tickbird.Core.Providers.DatabaseKind.TimescaleDb };
        return (new TimescaleDbClient(connection, runner), runner);
    }

    [Fact]
    public async Task Databases_AreListedFromTheCatalog()
    {
        var (client, runner) = Create(r => r.Answer("pg_database", new[] { "datname" }, new object[] { "app" }, new object[] { "metrics" }));
        Assert.Equal(new[] { "app", "metrics" }, await client.GetDatabaseNamesAsync());
        Assert.Contains("NOT datistemplate", runner.Queried.Single().Sql);
        Assert.Equal("postgres", runner.Queried.Single().Database);
    }

    [Fact]
    public async Task TheConnectionsDatabase_IsUsedForServerWideQueries_WhenItNamesOne()
    {
        var runner = new FakeSqlRunner().Answer("pg_database", new[] { "datname" }, new object[] { "tsdb" });
        var connection = new DatabaseConnection("1", "cloud", "h", 5432, "u", "p", true, "tsdb") { Kind = Tickbird.Core.Providers.DatabaseKind.TimescaleDb };
        await new TimescaleDbClient(connection, runner).GetDatabaseNamesAsync();
        Assert.Equal("tsdb", runner.Queried.Single().Database);     // accounts that cannot use "postgres" still work
    }

    [Fact]
    public async Task CreatingADatabase_AlsoInstallsTheExtension()
    {
        var (client, runner) = Create();
        var result = await client.CreateDatabaseAsync("metrics");
        Assert.True(result.Success);
        Assert.Equal(new[] { "CREATE DATABASE \"metrics\"", "CREATE EXTENSION IF NOT EXISTS timescaledb" }, runner.Executed.Select(c => c.Sql));
        Assert.Equal(new string?[] { "postgres", "metrics" }, runner.Executed.Select(c => c.Database));
    }

    [Fact]
    public async Task DroppingADatabase_EndsItsSessionsFirst_AndDropsInItsOwnCommand()
    {
        var (client, runner) = Create();
        await client.DropDatabaseAsync("metrics");
        var calls = runner.Executed.ToList();
        Assert.Equal(2, calls.Count);                               // DROP DATABASE cannot share a transaction block
        Assert.Contains("pg_terminate_backend", calls[0].Sql);
        Assert.Equal("metrics", calls[0].Parameters!["name"]);
        Assert.Equal("DROP DATABASE \"metrics\"", calls[1].Sql);
        Assert.All(calls, c => Assert.Equal("postgres", c.Database));
    }

    [Fact]
    public async Task DroppingTheDatabaseTheConnectionUses_ConnectsElsewhere()
    {
        var runner = new FakeSqlRunner();
        var connection = new DatabaseConnection("1", "x", "h", 5432, "u", "p", false, "metrics") { Kind = Tickbird.Core.Providers.DatabaseKind.TimescaleDb };
        await new TimescaleDbClient(connection, runner).DropDatabaseAsync("metrics");
        Assert.All(runner.Executed, c => Assert.Equal("postgres", c.Database));
    }

    [Fact]
    public async Task Tables_ShowTheirSchemaOnlyOutsidePublic_AndHideTimescaleInternals()
    {
        var (client, runner) = Create(r => r.Answer("pg_class", new[] { "schema_name", "table_name" },
            new object[] { "public", "cpu" }, new object[] { "metrics", "gps" }));
        Assert.Equal(new[] { "cpu", "metrics.gps" }, await client.GetMeasurementNamesAsync("app"));
        var sql = runner.Queried.Single().Sql;
        Assert.Contains("NOT LIKE '\\_timescaledb%'", sql);
        Assert.Contains("NOT LIKE 'timescaledb\\_%'", sql);
        Assert.Contains("'pg_catalog'", sql);
    }

    [Fact]
    public async Task TextColumnsAreTags_AndOtherColumnsAreFields()
    {
        var (client, runner) = Create(r => r.Answer("pg_attribute", new[] { "column_name", "data_type" },
            new object[] { "time", "timestamp with time zone" }, new object[] { "host", "text" },
            new object[] { "region", "character varying(8)" }, new object[] { "usage", "double precision" }, new object[] { "ok", "boolean" }));

        Assert.Equal(new[] { "host", "region" }, await client.GetTagKeysAsync("app", "metrics.cpu"));
        var fields = (await client.GetFieldKeysAsync("app", "metrics.cpu")).ToList();
        Assert.Equal(new[] { "time", "usage", "ok" }, fields.Select(f => f.Name));
        Assert.Equal("double precision", fields.Single(f => f.Name == "usage").Type);
        Assert.Equal("metrics", runner.Queried.First().Parameters!["schema"]);
        Assert.Equal("cpu", runner.Queried.First().Parameters!["table"]);
    }

    [Fact]
    public async Task TagValues_AreDistinctAndLimited_WithTheNamesQuoted()
    {
        var (client, runner) = Create(r => r.Answer("DISTINCT", new[] { "host" }, new object[] { "db-1" }, new object[] { "web-1" }));
        var values = (await client.GetTagValuesAsync("app", "cpu", "ho\"st")).ToList();
        Assert.Equal(new[] { "db-1", "web-1" }, values.Select(v => v.Value));
        Assert.All(values, v => Assert.Equal("ho\"st", v.Name));
        var sql = runner.Queried.Single().Sql;
        Assert.Contains("\"ho\"\"st\"", sql);
        Assert.Contains("FROM \"public\".\"cpu\"", sql);
        Assert.EndsWith("LIMIT 1000", sql);
    }

    [Fact]
    public async Task TheWholeSchema_ComesFromOneQuery_ForIntelliSense()
    {
        var (client, runner) = Create(r => r.Answer("pg_attribute", new[] { "schema_name", "table_name", "column_name", "data_type" },
            new object[] { "public", "cpu", "time", "timestamp with time zone" }, new object[] { "public", "cpu", "host", "text" },
            new object[] { "public", "cpu", "usage", "double precision" }, new object[] { "metrics", "gps", "lat", "double precision" }));
        var schema = await client.GetDatabaseSchemaAsync("app");
        Assert.Equal(1, runner.Queried.Count());
        Assert.Equal(new[] { "cpu", "metrics.gps" }, schema.Select(s => s.Name));
        Assert.Equal(new[] { "host" }, schema[0].Tags);
        Assert.Equal(new[] { "time", "usage" }, schema[0].Fields.Select(f => f.Name));
    }

    [Fact]
    public async Task Chunks_AreTheSeries_WrittenLikeSeriesKeys()
    {
        var start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var (client, runner) = Create(r => r.Answer("timescaledb_information.chunks", new[] { "chunk_schema", "chunk_name", "range_start", "range_end", "is_compressed" },
            new object[] { "_timescaledb_internal", "_hyper_1_2_chunk", start, start.AddDays(7), true }));

        var series = (await client.GetSeriesNamesAsync("app", "cpu")).ToList();

        Assert.Equal("cpu,chunk=_hyper_1_2_chunk,range_start=2026-10-01 00:00:00,range_end=2026-10-08 00:00:00,compressed=true", series.Single());
        var table = Tickbird.Core.Navigation.SeriesTable.Parse("cpu", series);
        Assert.Equal(new[] { "chunk", "range_start", "range_end", "compressed" }, table.Columns);
        Assert.Equal("public", runner.Queried.Single().Parameters!["schema"]);
    }

    [Fact]
    public async Task APlainTable_ShowsItsTagCombinationsAsSeries()
    {
        var (client, runner) = Create(r => r
            .Answer("pg_attribute", new[] { "column_name", "data_type" }, new object[] { "host", "text" }, new object[] { "usage", "double precision" })
            .Answer("SELECT DISTINCT ',host='", new[] { "key" }, new object[] { ",host=web-1" }));
        var series = (await client.GetSeriesNamesAsync("app", "plain")).ToList();
        Assert.Equal("plain,host=web-1", series.Single());
    }

    [Fact]
    public async Task DropSeries_EmptiesTheTable()
    {
        var (client, runner) = Create();
        await client.DropSeriesAsync("app", "metrics.cpu");
        Assert.Equal("TRUNCATE TABLE \"metrics\".\"cpu\"", runner.Executed.Single().Sql);
        await Assert.ThrowsAsync<NotSupportedException>(() => client.DropSeriesAsync("app", null));
    }

    [Fact]
    public async Task DroppingATable_UsesTheKeywordOfWhatItIs()
    {
        var (client, runner) = Create(r => r.Answer("relkind", new[] { "relkind" }, new object[] { "m" }));
        await client.DropMeasurementAsync("app", "daily");
        Assert.Equal("DROP MATERIALIZED VIEW IF EXISTS \"public\".\"daily\"", runner.Executed.Single().Sql);
    }

    // ------------------------------------------------------------------ retention

    [Fact]
    public async Task RetentionPolicies_ArePerHypertable()
    {
        var (client, runner) = Create(r => r.Answer("policy_retention", new[] { "s", "t", "drop_after", "chunk_interval" },
            new object[] { "public", "cpu", "30 days", "7 days" }, new object[] { "metrics", "gps", "90 days", "1 day" }));
        var policies = (await client.GetRetentionPoliciesAsync("app")).ToList();
        Assert.Equal(new[] { "cpu", "metrics.gps" }, policies.Select(p => p.Name));
        Assert.Equal("30 days", policies[0].Duration);
        Assert.Equal("7 days", policies[0].ShardGroupDuration);
        Assert.All(policies, p => { Assert.Equal("app", p.Database); Assert.False(p.Default); });
    }

    [Fact]
    public async Task CreatingARetentionPolicy_ReplacesTheOne_AndConvertsTheDuration()
    {
        var (client, runner) = Create();
        var result = await client.CreateRetentionPolicyAsync("app", "metrics.cpu", "30d", 1);
        Assert.True(result.Success);
        var call = runner.Executed.Single();
        Assert.Contains("remove_retention_policy(@table::regclass, if_exists => true)", call.Sql);
        Assert.Contains("add_retention_policy(@table::regclass, drop_after => INTERVAL '30 days')", call.Sql);
        Assert.Equal("\"metrics\".\"cpu\"", call.Parameters!["table"]);
    }

    [Fact]
    public async Task ABadDuration_IsReportedAndNothingRuns()
    {
        var (client, runner) = Create();
        var result = await client.CreateRetentionPolicyAsync("app", "cpu", "soon", 1);
        Assert.False(result.Success);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task DroppingARetentionPolicy_RemovesIt()
    {
        var (client, runner) = Create();
        await client.DropRetentionPolicyAsync("app", "cpu");
        Assert.Contains("remove_retention_policy", runner.Executed.Single().Sql);
    }

    // ------------------------------------------------------------------ continuous aggregates

    [Fact]
    public async Task ContinuousAggregates_AreTheContinuousQueries()
    {
        var (client, runner) = Create(r => r.Answer("continuous_aggregates", new[] { "s", "n", "def" },
            new object[] { "public", "cpu_1h", " SELECT time_bucket('1 hour', time) AS bucket FROM cpu GROUP BY 1;" }));
        var cq = (await client.GetContinousQueriesAsync("app")).Single();
        Assert.Equal("cpu_1h", cq.Name);
        Assert.StartsWith("CREATE MATERIALIZED VIEW \"public\".\"cpu_1h\"\nWITH (timescaledb.continuous) AS\nSELECT time_bucket", cq.Query);
    }

    [Fact]
    public void TheBucketSelect_GroupsByTimeAndTags()
    {
        var sql = TimescaleDbClient.BucketSelect("metrics.cpu", "ts", "10m", new[] { "host", "region" }, new[] { "avg(\"usage\") AS \"usage\"" });
        Assert.Equal("SELECT time_bucket(INTERVAL '10 minutes', \"ts\") AS \"time\", \"host\", \"region\", avg(\"usage\") AS \"usage\"\n" +
                     "FROM \"metrics\".\"cpu\"\nGROUP BY 1, \"host\", \"region\"", sql);
        Assert.Throws<FormatException>(() => TimescaleDbClient.BucketSelect("cpu", "time", "10m", null, Array.Empty<string>()));
    }

    [Fact]
    public async Task CreatingAContinuousAggregate_RunsEachStatementOnItsOwn()
    {
        var (client, runner) = Create(r => r.Answer("dimensions", new[] { "column_name" }, new object[] { "ts" }));
        var result = await client.CreateContinuousQueryAsync(new ContinuousQueryParams
        {
            Name = "cpu_1h", Database = "app", Source = "cpu", Interval = "1h", Tags = new[] { "host" },
            SubQueries = new[] { "avg(\"usage\") AS \"usage\"" }
        });

        Assert.True(result.Success, result.Body);
        var calls = runner.Executed.ToList();
        Assert.Equal(2, calls.Count);                                 // a continuous aggregate cannot be created in a transaction block
        Assert.StartsWith("CREATE MATERIALIZED VIEW \"public\".\"cpu_1h\" WITH (timescaledb.continuous) AS", calls[0].Sql);
        Assert.Contains("time_bucket(INTERVAL '1 hours', \"ts\")", calls[0].Sql);
        Assert.EndsWith("WITH NO DATA", calls[0].Sql);
        Assert.Contains("add_continuous_aggregate_policy(@view::regclass", calls[1].Sql);
        Assert.Contains("schedule_interval => INTERVAL '1 hours'", calls[1].Sql);
        Assert.Equal("\"public\".\"cpu_1h\"", calls[1].Parameters!["view"]);
    }

    [Fact]
    public async Task AContinuousAggregateWithoutAggregates_IsRejected()
    {
        var (client, runner) = Create();
        var result = await client.CreateContinuousQueryAsync(new ContinuousQueryParams { Name = "x", Database = "app", Source = "cpu", Interval = "1h", SubQueries = Array.Empty<string>() });
        Assert.False(result.Success);
        Assert.Empty(runner.Executed);
    }

    [Fact]
    public async Task DroppingAContinuousAggregate_DropsTheView()
    {
        var (client, runner) = Create();
        await client.DropContinuousQueryAsync("app", "cpu_1h");
        Assert.Equal("DROP MATERIALIZED VIEW IF EXISTS \"public\".\"cpu_1h\"", runner.Executed.Single().Sql);
    }

    // ------------------------------------------------------------------ backfill

    private static BackfillParams Backfill(string destination) => new()
    {
        Source = "cpu", Destination = destination, Interval = "1h", Tags = new[] { "host" },
        SubQueries = new[] { "avg(\"usage\") AS \"usage\"" },
        FromTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), ToTime = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
        Filters = new List<string> { "\"region\" = 'eu'" }
    };

    [Fact]
    public async Task Backfill_CreatesTheDestination_WhenItIsMissing()
    {
        var (client, runner) = Create(r => r.Answer("to_regclass", new[] { "exists" }, new object[] { false }));
        await client.BackfillAsync("app", Backfill("cpu_1h"));
        var sql = runner.Executed.Single().Sql;
        Assert.StartsWith("CREATE TABLE \"public\".\"cpu_1h\" AS\nSELECT time_bucket(INTERVAL '1 hours', \"time\")", sql);
        Assert.Contains("WHERE \"time\" >= '2026-01-01T00:00:00Z'::timestamptz AND \"time\" < '2026-02-01T00:00:00Z'::timestamptz AND (\"region\" = 'eu')", sql);
    }

    [Fact]
    public async Task Backfill_AddsRows_WhenTheDestinationExists()
    {
        var (client, runner) = Create(r => r.Answer("to_regclass", new[] { "exists" }, new object[] { true }));
        await client.BackfillAsync("app", Backfill("cpu_1h"));
        Assert.StartsWith("INSERT INTO \"public\".\"cpu_1h\"\nSELECT time_bucket", runner.Executed.Single().Sql);
    }

    // ------------------------------------------------------------------ users and privileges

    [Fact]
    public async Task Roles_AreTheUsers()
    {
        var (client, runner) = Create(r => r.Answer("pg_roles", new[] { "rolname", "rolsuper" }, new object[] { "admin", true }, new object[] { "reader", false }));
        var users = (await client.GetUsersAsync()).ToList();
        Assert.Equal(new[] { "admin", "reader" }, users.Select(u => u.Name));
        Assert.Equal(new[] { true, false }, users.Select(u => u.IsAdmin));
        Assert.Contains("rolcanlogin", runner.Queried.Single().Sql);
    }

    [Fact]
    public async Task UserCommands_QuoteNamesAndPasswords()
    {
        var (client, runner) = Create();
        await client.CreateUserAsync("o\"neil", "p'w", isAdmin: false);
        await client.CreateUserAsync("boss", "x", isAdmin: true);
        await client.SetPasswordAsync("boss", "n'ew");
        await client.GrantAdministratorAsync("reader");
        await client.RevokeAdministratorAsync("reader");
        await client.DropUserAsync("boss");
        Assert.Equal(new[]
        {
            "CREATE ROLE \"o\"\"neil\" WITH LOGIN PASSWORD 'p''w' NOSUPERUSER",
            "CREATE ROLE \"boss\" WITH LOGIN PASSWORD 'x' SUPERUSER",
            "ALTER ROLE \"boss\" WITH PASSWORD 'n''ew'",
            "ALTER ROLE \"reader\" SUPERUSER",
            "ALTER ROLE \"reader\" NOSUPERUSER",
            "DROP ROLE \"boss\"",
        }, runner.Executed.Select(c => c.Sql));
    }

    [Fact]
    public async Task DatabaseErrors_BecomeAFailedResult_NotACrash()
    {
        var runner = new ThrowingRunner();
        var client = new TimescaleDbClient(new DatabaseConnection("1", "x", "h", 5432, "u", "p") { Kind = Tickbird.Core.Providers.DatabaseKind.TimescaleDb }, runner);
        var result = await client.CreateUserAsync("a", "b", false);
        Assert.False(result.Success);
        Assert.Equal("role \"a\" already exists", result.Body);
    }

    private sealed class ThrowingRunner : ISqlRunner
    {
        public Task<IReadOnlyList<ResultSeries>> QueryAsync(string? database, string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default) =>
            throw new Npgsql.PostgresException("role \"a\" already exists", "ERROR", "ERROR", "42710");

        public Task<int> ExecuteAsync(string? database, string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default) =>
            throw new Npgsql.PostgresException("role \"a\" already exists", "ERROR", "ERROR", "42710");
    }

    [Fact]
    public void Grants_ReplaceEarlierPrivileges_ForExistingAndFutureTables()
    {
        var statements = PostgresClientBase.GrantStatements("app", "reader", DatabasePrivileges.Read, new[] { "public" }).ToList();
        Assert.Equal(new[]
        {
            "GRANT CONNECT ON DATABASE \"app\" TO \"reader\"",
            "GRANT USAGE ON SCHEMA \"public\" TO \"reader\"",
            "REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA \"public\" FROM \"reader\"",
            "ALTER DEFAULT PRIVILEGES IN SCHEMA \"public\" REVOKE ALL PRIVILEGES ON TABLES FROM \"reader\"",
            "GRANT SELECT ON ALL TABLES IN SCHEMA \"public\" TO \"reader\"",
            "ALTER DEFAULT PRIVILEGES IN SCHEMA \"public\" GRANT SELECT ON TABLES TO \"reader\"",
        }, statements);

        Assert.Contains(PostgresClientBase.GrantStatements("app", "w", DatabasePrivileges.Write, new[] { "public" }), s => s.Contains("GRANT INSERT, UPDATE, DELETE ON ALL TABLES"));
        Assert.Contains(PostgresClientBase.GrantStatements("app", "a", DatabasePrivileges.All, new[] { "public" }), s => s.Contains("GRANT ALL PRIVILEGES ON ALL TABLES"));
        Assert.DoesNotContain(PostgresClientBase.GrantStatements("app", "n", DatabasePrivileges.None, new[] { "public" }), s => s.StartsWith("GRANT SELECT") || s.StartsWith("GRANT ALL"));
    }

    [Fact]
    public async Task Privileges_AreSummarisedPerDatabase()
    {
        var (client, runner) = Create(r => r
            .Answer("pg_database", new[] { "datname" }, new object[] { "app" })
            .Answer("has_table_privilege", new[] { "tables", "readable", "writable" }, new object[] { 3L, 3L, 3L }));
        var grants = (await client.GetPrivilegesAsync("reader")).ToList();
        Assert.Equal("app", grants.Single().Database);
        Assert.Equal(DatabasePrivileges.All, grants.Single().Privilege);
    }

    [Theory]
    [InlineData(3L, 3L, 0L, DatabasePrivileges.Read)]
    [InlineData(3L, 0L, 3L, DatabasePrivileges.Write)]
    [InlineData(3L, 1L, 1L, null)]
    [InlineData(0L, 0L, 0L, null)]
    public async Task Privileges_NeedEveryTable(long tables, long readable, long writable, DatabasePrivileges? expected)
    {
        var (client, _) = Create(r => r
            .Answer("pg_database", new[] { "datname" }, new object[] { "app" })
            .Answer("has_table_privilege", new[] { "tables", "readable", "writable" }, new object[] { tables, readable, writable }));
        var grants = (await client.GetPrivilegesAsync("reader")).ToList();
        if (expected == null) Assert.Empty(grants);
        else Assert.Equal(expected, grants.Single().Privilege);
    }

    // ------------------------------------------------------------------ running queries, writes, server

    [Fact]
    public async Task RunningQueries_AreTheActiveSessions_AndCanBeCancelled()
    {
        var (client, runner) = Create(r => r.Answer("pg_stat_activity", new[] { "pid", "datname", "duration", "query" },
            new object[] { 4242, "app", "00:00:07", "SELECT pg_sleep(60)" }));
        var running = (await client.GetRunningQueriesAsync()).Single();
        Assert.Equal((4242, "app", "00:00:07", "SELECT pg_sleep(60)"), (running.PID, running.Database, running.Duration, running.Query));
        await client.KillQueryAsync(4242);
        Assert.Equal("SELECT pg_cancel_backend(@pid)", runner.Executed.Single().Sql);
        Assert.Equal(4242, runner.Executed.Single().Parameters!["pid"]);
    }

    [Fact]
    public void AnInsert_UsesParameters_ForEveryValue()
    {
        var (client, _) = Create();
        var time = new DateTime(2026, 10, 21, 7, 0, 0, DateTimeKind.Utc);
        var (sql, parameters) = client.BuildInsert("metrics.cpu", new Dictionary<string, object> { ["host"] = "web-1" },
            new Dictionary<string, object> { ["usage"] = 41.5 }, time);
        Assert.Equal("INSERT INTO \"metrics\".\"cpu\" (\"time\", \"host\", \"usage\") VALUES (@p0_0, @p0_1, @p0_2)", sql);
        Assert.Equal(time, parameters["p0_0"]);
        Assert.Equal("web-1", parameters["p0_1"]);
        Assert.Equal(41.5, parameters["p0_2"]);
    }

    [Fact]
    public async Task WritingSeveralPoints_IsOneCommand_WithUniqueParameters()
    {
        var (client, runner) = Create();
        var points = new[]
        {
            new DataPoint("cpu", new Dictionary<string, object> { ["host"] = "a" }, new Dictionary<string, object> { ["usage"] = 1.0 }),
            new DataPoint("cpu", new Dictionary<string, object> { ["host"] = "b" }, new Dictionary<string, object> { ["usage"] = 2.0 }),
        };
        var result = await client.WriteAsync("app", points);
        Assert.True(result.Success);
        var call = runner.Executed.Single();
        Assert.Equal(2, call.Sql.Split(';').Length);
        Assert.Equal(6, call.Parameters!.Count);
        Assert.Contains("@p1_2", call.Sql);
    }

    [Fact]
    public async Task Ping_ReportsTheTimescaleAndPostgresVersions()
    {
        var (client, _) = Create(r => r
            .Answer("SELECT version()", new[] { "version" }, new object[] { "PostgreSQL 16.3 (Debian 16.3-1.pgdg120+1) on x86_64-pc-linux-gnu" })
            .Answer("extversion", new[] { "extversion" }, new object[] { "2.15.2" }));
        var ping = await client.PingAsync();
        Assert.True(ping.Success);
        Assert.Equal("2.15.2 on PostgreSQL 16.3", ping.Version);
    }

    [Fact]
    public async Task Ping_SaysWhenTheExtensionIsMissing()
    {
        var (client, _) = Create(r => r.Answer("SELECT version()", new[] { "version" }, new object[] { "PostgreSQL 15.5 on x" }));
        Assert.Equal("PostgreSQL 15.5 (TimescaleDB extension not installed)", (await client.PingAsync()).Version);
    }

    [Fact]
    public async Task Diagnostics_AreNameValueRows()
    {
        var started = new DateTime(2026, 10, 14, 5, 0, 0, DateTimeKind.Utc);
        var now = started.AddDays(7).AddHours(2);
        var (client, _) = Create(r => r
            .Answer("pg_postmaster_start_time", new[] { "server_version", "version", "started", "current_time", "database", "user_name", "pid", "address", "port", "time_zone", "max_connections", "connections" },
                new object[] { "16.3", "PostgreSQL 16.3 on x", started, now, "app", "app", 2214, "10.0.0.5", "5432", "UTC", "100", "7" })
            .Answer("extversion", new[] { "extversion" }, new object[] { "2.15.2" })
            .Answer("hypertables", new[] { "count" }, new object[] { 4L }));
        var diagnostics = await client.GetDiagnosticsAsync();
        var rows = diagnostics.Properties.ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal("16.3", rows["Server version"]);
        Assert.Equal("7d 2h 0m 0s", rows["Uptime"]);
        Assert.Equal("7 of 100", rows["Connections"]);
        Assert.Equal("2.15.2", rows["TimescaleDB version"]);
        Assert.Equal("4", rows["Hypertables"]);
        Assert.Equal(2214, diagnostics.PID);
        Assert.Equal("localhost", diagnostics.Hostname);
    }

    [Fact]
    public async Task Statistics_AreCustomCategories_AndSkipEmptyOnes()
    {
        var (client, _) = Create(r => r
            .Answer("pg_stat_database", new[] { "datname", "numbackends" }, new object[] { "app", 3 })
            .Answer("timescaledb_information.hypertables", new[] { "hypertable_name" }, new object[] { "cpu" }));
        var stats = await client.GetStatsAsync();
        Assert.True(stats.Custom.ContainsKey("Databases"));
        Assert.True(stats.Custom.ContainsKey("Hypertables"));
        Assert.False(stats.Custom.ContainsKey("Tables"));        // the fake had no rows for it
    }

    [Fact]
    public async Task Queries_RunAsWritten_AndReturnEveryResultSet()
    {
        var (client, runner) = Create(r => r.Answer("SELECT", new[] { "a" }, new object[] { 1L }));
        var result = (await client.QueryAsync("app", "SELECT 1")).ToList();
        Assert.Single(result);
        Assert.Equal("SELECT 1", runner.Queried.Single().Sql);
        Assert.Equal("app", runner.Queried.Single().Database);
    }

    [Fact]
    public void ThePolicyOfTheDatabase_NamesTheTimescaleThings()
    {
        var profile = TimescaleDbClient.Profile;
        Assert.True(profile.RetentionPerTable);
        Assert.False(profile.ContinuousQueryWritesToDestination);
        Assert.Equal("SELECT * FROM \"metrics\".\"cpu\" ORDER BY 1 DESC LIMIT 100", profile.SampleQuery("metrics.cpu"));
        Assert.Contains(profile.CompletionExtras, e => e.Text == "time_bucket()" && e.IsFunction);
        Assert.True(profile.IsSystemDatabase("POSTGRES"));
        Assert.False(profile.IsSystemDatabase("metrics"));
    }
}

public class ConnectionStringTests
{
    [Fact]
    public void TheConnection_UsesTheAccount_AndPicksTheSslMode()
    {
        var plain = NpgsqlRunner.BuildConnectionString(new DatabaseConnection("1", "x", "db.example.com", 6543, "app", "s3cret", false), "metrics", false);
        Assert.Equal("db.example.com", plain.Host);
        Assert.Equal(6543, plain.Port);
        Assert.Equal("metrics", plain.Database);
        Assert.Equal("app", plain.Username);
        Assert.Equal(Npgsql.SslMode.Prefer, plain.SslMode);
        Assert.Equal("Tickbird", plain.ApplicationName);
        Assert.Equal(0, plain.CommandTimeout);

        var secure = NpgsqlRunner.BuildConnectionString(new DatabaseConnection("1", "x", "h", 5432, "app", "p", true), "metrics", true);
        Assert.Equal(Npgsql.SslMode.Require, secure.SslMode);
        Assert.True(secure.TrustServerCertificate);
    }

    [Fact]
    public void ACellValue_IsSomethingTheGridCanShow()
    {
        Assert.Null(NpgsqlRunner.ToCellValue(DBNull.Value));
        Assert.Equal(5L, NpgsqlRunner.ToCellValue(5L));
        Assert.Equal(DateTimeKind.Utc, ((DateTime)NpgsqlRunner.ToCellValue(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified))!).Kind);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), NpgsqlRunner.ToCellValue(new DateTimeOffset(2026, 1, 1, 2, 0, 0, TimeSpan.FromHours(2))));
        Assert.Equal("{1,2,3}", NpgsqlRunner.ToCellValue(new[] { 1, 2, 3 }));
        Assert.Equal("\\xdeadbeef", NpgsqlRunner.ToCellValue(new byte[] { 0xde, 0xad, 0xbe, 0xef }));
        Assert.Equal("00000000-0000-0000-0000-000000000000", NpgsqlRunner.ToCellValue(Guid.Empty));
    }
}
