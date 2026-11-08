using Npgsql;
using Tickbird.Core.Data;
using Tickbird.Core.Data.Sql;
using Tickbird.Core.Navigation;
using Tickbird.Core.Providers;

namespace Tickbird.Tests;

/// <summary>A runner that fails the Nth statement it executes (1-based) with a database error, to check what stops.</summary>
internal sealed class FailingRunner : ISqlRunner
{
    private readonly int failAt;
    private readonly Exception error;
    public List<string> Statements { get; } = new();

    public FailingRunner(int failAt, Exception? error = null)
    {
        this.failAt = failAt;
        this.error = error ?? new PostgresException("boom", "ERROR", "ERROR", "XX000");
    }

    public Task<IReadOnlyList<ResultSeries>> QueryAsync(string? database, string sql,
        IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        Statements.Add(sql);
        return Statements.Count == failAt ? throw error : Task.FromResult<IReadOnlyList<ResultSeries>>(new List<ResultSeries>());
    }

    public Task<int> ExecuteAsync(string? database, string sql,
        IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        Statements.Add(sql);
        return Statements.Count == failAt ? throw error : Task.FromResult(0);
    }
}

public class CrateDbSqlHelperTests
{
    [Fact]
    public void TheDefaultSchema_IsDoc_ForCrateDb_AndPublicOtherwise()
    {
        Assert.Equal(("doc", "cpu"), PostgresSql.SplitQualified("cpu", "doc"));
        Assert.Equal(("metrics", "cpu"), PostgresSql.SplitQualified("metrics.cpu", "doc"));
        Assert.Equal("\"doc\".\"cpu\"", PostgresSql.Table("cpu", "doc"));
        Assert.Equal("\"public\".\"cpu\"", PostgresSql.Table("cpu"));
        Assert.Equal("cpu", PostgresSql.DisplayName("doc", "cpu", "doc"));
        Assert.Equal("doc.cpu", PostgresSql.DisplayName("doc", "cpu"));
        Assert.Equal("metrics.cpu", PostgresSql.DisplayName("metrics", "cpu", "doc"));
    }

    [Theory]
    [InlineData("o'neil", "'o''neil'")]
    [InlineData("a\\b", "'a\\b'")]          // CrateDB reads a backslash literally: no E'..' form
    [InlineData("", "''")]
    [InlineData("x'; DROP USER crate; --", "'x''; DROP USER crate; --'")]
    public void StringLiterals_OnlyDoubleQuotes(string value, string expected) =>
        Assert.Equal(expected, PostgresSql.StringLiteral(value));

    [Fact]
    public void ANullStringLiteral_IsNull() => Assert.Equal("NULL", PostgresSql.StringLiteral(null));

    [Fact]
    public void CrateDb_UsesTheNoTypeLoadingMode_OfNpgsql()
    {
        var connection = new DatabaseConnection("1", "c", "host", 5432, "crate", "") { Kind = DatabaseKind.CrateDb };
        Assert.Equal(ServerCompatibilityMode.NoTypeLoading,
            NpgsqlRunner.BuildConnectionString(connection, "crate", false, ServerCompatibilityMode.NoTypeLoading).ServerCompatibilityMode);
        Assert.Equal(ServerCompatibilityMode.None, NpgsqlRunner.BuildConnectionString(connection, "postgres", false).ServerCompatibilityMode);
    }
}

public class CrateDbClientTests
{
    private static (CrateDbClient Client, FakeSqlRunner Runner) Create(Action<FakeSqlRunner>? answers = null, string? database = null)
    {
        var runner = new FakeSqlRunner();
        answers?.Invoke(runner);
        var connection = new DatabaseConnection("1", "crate", "localhost", 5432, "crate", "", false, database) { Kind = DatabaseKind.CrateDb };
        return (new CrateDbClient(connection, runner), runner);
    }

    // ------------------------------------------------------------------ database and tables

    [Fact]
    public async Task TheOneDatabase_IsCrate_AndTheServerIsAskedSomething()
    {
        var (client, runner) = Create(r => r.Answer("sys.cluster", new[] { "name" }, new object[] { "my-cluster" }));
        Assert.Equal(new[] { "crate" }, await client.GetDatabaseNamesAsync());
        Assert.Equal(CrateDbClient.ConnectivitySql, runner.Queried.Single().Sql);   // so "Test connection" checks the server
        Assert.Equal("crate", runner.Queried.Single().Database);
    }

    [Fact]
    public async Task TheConnectionsDatabaseField_IsIgnored_BecauseThereIsOneDatabase()
    {
        var (client, runner) = Create(database: "something-else");
        await client.GetDatabaseNamesAsync();
        await client.GetUsersAsync();
        Assert.All(runner.Queried, call => Assert.Equal("crate", call.Database));
    }

    [Fact]
    public async Task CreatingAndDroppingADatabase_IsNotSupported()
    {
        var (client, _) = Create();
        await Assert.ThrowsAsync<NotSupportedException>(() => client.CreateDatabaseAsync("x"));
        await Assert.ThrowsAsync<NotSupportedException>(() => client.DropDatabaseAsync("x"));
    }

    [Fact]
    public async Task Tables_AreListed_WithoutTheDocSchemaAndWithoutTheServersOwnSchemas()
    {
        var (client, runner) = Create(r => r.Answer("information_schema.tables", new[] { "table_schema", "table_name" },
            new object[] { "doc", "cpu" }, new object[] { "doc", "mem" }, new object[] { "metrics", "cpu" }));
        Assert.Equal(new[] { "cpu", "mem", "metrics.cpu" }, await client.GetMeasurementNamesAsync("crate"));
        var sql = runner.Queried.Single().Sql;
        Assert.Contains("NOT IN ('sys', 'information_schema', 'pg_catalog', 'blob')", sql);
        Assert.Contains("(table_schema <> 'doc')", sql);        // doc first
        Assert.DoesNotContain("pg_class", sql);                 // not the PostgreSQL catalog
    }

    [Fact]
    public async Task ATable_IsDropped_AndAViewIsDroppedAsAView()
    {
        var (client, runner) = Create(r => r.Answer("table_type", new[] { "table_type" }, new object[] { "BASE TABLE" }));
        await client.DropMeasurementAsync("crate", "cpu");
        Assert.Equal("DROP TABLE IF EXISTS \"doc\".\"cpu\"", runner.Executed.Single().Sql);

        var (viewClient, viewRunner) = Create(r => r.Answer("table_type", new[] { "table_type" }, new object[] { "VIEW" }));
        await viewClient.DropMeasurementAsync("crate", "metrics.v");
        Assert.Equal("DROP VIEW IF EXISTS \"metrics\".\"v\"", viewRunner.Executed.Single().Sql);
        Assert.Equal("metrics", viewRunner.Queried.Single().Parameters!["schema"]);
        Assert.Equal("v", viewRunner.Queried.Single().Parameters!["table"]);
    }

    [Fact]
    public async Task DroppingARelationThatIsUnknown_IsADropTable()
    {
        var (client, runner) = Create();
        await client.DropMeasurementAsync("crate", "gone");
        Assert.Equal("DROP TABLE IF EXISTS \"doc\".\"gone\"", runner.Executed.Single().Sql);
    }

    [Fact]
    public async Task DeletingAllRows_UsesDelete_BecauseThereIsNoTruncate()
    {
        var (client, runner) = Create();
        var result = await client.DropSeriesAsync("crate", "metrics.cpu");
        Assert.True(result.Success);
        Assert.Equal("DELETE FROM \"metrics\".\"cpu\"", runner.Executed.Single().Sql);
        await Assert.ThrowsAsync<NotSupportedException>(() => client.DropSeriesAsync("crate"));
    }

    // ------------------------------------------------------------------ columns

    [Fact]
    public async Task TextAndIpColumns_AreTags_TheRestAreFields()
    {
        var (client, runner) = Create(r => r.Answer("information_schema.columns", new[] { "column_name", "data_type" },
            new object[] { "host", "text" }, new object[] { "addr", "ip" }, new object[] { "ts", "timestamp with time zone" },
            new object[] { "usage", "double precision" }, new object[] { "meta", "object" }, new object[] { "ok", "boolean" }));

        Assert.Equal(new[] { "host", "addr" }, await client.GetTagKeysAsync("crate", "cpu"));
        var fields = (await client.GetFieldKeysAsync("crate", "cpu")).ToList();
        Assert.Equal(new[] { "ts", "usage", "meta", "ok" }, fields.Select(f => f.Name));
        Assert.Equal("double precision", fields.Single(f => f.Name == "usage").Type);

        var call = runner.Queried.First();
        Assert.Equal("doc", call.Parameters!["schema"]);
        Assert.Equal("cpu", call.Parameters["table"]);
        Assert.Contains("NOT LIKE '%[%'", call.Sql);                 // sub-columns of objects are not columns of their own
        Assert.Contains("ORDER BY ordinal_position", call.Sql);
    }

    [Fact]
    public async Task ASchemaQualifiedTable_ReadsItsColumnsFromThatSchema()
    {
        var (client, runner) = Create();
        await client.GetTagKeysAsync("crate", "metrics.cpu");
        Assert.Equal("metrics", runner.Queried.Single().Parameters!["schema"]);
    }

    [Fact]
    public async Task TheSchemaForIntelliSense_GroupsColumnsPerTable()
    {
        var (client, runner) = Create(r => r.Answer("information_schema.columns", new[] { "table_schema", "table_name", "column_name", "data_type" },
            new object[] { "doc", "cpu", "host", "text" }, new object[] { "doc", "cpu", "usage", "double precision" },
            new object[] { "metrics", "mem", "free", "bigint" }));
        var schema = await client.GetDatabaseSchemaAsync("crate");
        Assert.Equal(new[] { "cpu", "metrics.mem" }, schema.Select(t => t.Name));
        Assert.Equal(new[] { "host" }, schema[0].Tags);
        Assert.Equal(new[] { "usage" }, schema[0].Fields.Select(f => f.Name));
        Assert.DoesNotContain("pg_attribute", runner.Queried.Single().Sql);
    }

    [Fact]
    public async Task TagValues_AreDistinct_Quoted_AndLimited()
    {
        var (client, runner) = Create(r => r.Answer("DISTINCT", new[] { "host" }, new object[] { "a" }, new object[] { "b" }));
        var values = (await client.GetTagValuesAsync("crate", "cpu", "host")).ToList();
        Assert.Equal(new[] { "a", "b" }, values.Select(v => v.Value));
        Assert.All(values, v => Assert.Equal("host", v.Name));
        var sql = runner.Queried.Single().Sql;
        Assert.Contains("FROM \"doc\".\"cpu\"", sql);
        Assert.Contains("\"host\"::text", sql);
        Assert.EndsWith($"LIMIT {PostgresClientBase.MaxTagValues}", sql);
    }

    [Fact]
    public async Task ATagNameWithAQuote_CannotBreakOutOfTheIdentifier()
    {
        var (client, runner) = Create();
        await client.GetTagValuesAsync("crate", "cpu", "a\"; DROP TABLE x; --");
        Assert.Contains("\"a\"\"; DROP TABLE x; --\"", runner.Queried.Single().Sql);
    }

    // ------------------------------------------------------------------ partitions as series

    [Fact]
    public async Task Partitions_AreTheSeries_OfAPartitionedTable()
    {
        var (client, runner) = Create(r => r.Answer("table_partitions", new[] { "partition_ident", "values" },
            new object[] { "04732cpp6ksjcc9i60o30c1g", "{day=1}" }, new object[] { "04732cpp6ksjcc9i60o30c1h", "{day=2}" }));
        var series = (await client.GetSeriesNamesAsync("crate", "events")).ToList();
        Assert.Equal(new[]
        {
            "events,partition=04732cpp6ksjcc9i60o30c1g,values={day=1}",
            "events,partition=04732cpp6ksjcc9i60o30c1h,values={day=2}"
        }, series);
        Assert.Equal("doc", runner.Queried.Single().Parameters!["schema"]);
    }

    [Fact]
    public async Task ATableWithoutPartitions_ShowsItsTagCombinations()
    {
        var (client, runner) = Create(r => r
            .Answer("information_schema.columns", new[] { "column_name", "data_type" }, new object[] { "host", "text" }, new object[] { "v", "integer" })
            .Answer("SELECT DISTINCT", new[] { "k" }, new object[] { ",host=a" }, new object[] { ",host=b" }));
        Assert.Equal(new[] { "cpu,host=a", "cpu,host=b" }, await client.GetSeriesNamesAsync("crate", "cpu"));
        Assert.Contains(runner.Queried, c => c.Sql.Contains("table_partitions"));
    }

    [Fact]
    public async Task NoTable_HasNoSeries()
    {
        var (client, runner) = Create();
        Assert.Empty(await client.GetSeriesNamesAsync("crate"));
        Assert.Empty(runner.Calls);
    }

    // ------------------------------------------------------------------ writing

    [Fact]
    public void AnInsert_QualifiesTheTableWithDoc_AndUsesParameters()
    {
        var (client, _) = Create();
        var time = new DateTime(2026, 10, 21, 7, 0, 0, DateTimeKind.Utc);
        var (sql, parameters) = client.BuildInsert("cpu", new Dictionary<string, object> { ["host"] = "web-1" },
            new Dictionary<string, object> { ["usage"] = 41.5 }, time);
        Assert.Equal("INSERT INTO \"doc\".\"cpu\" (\"time\", \"host\", \"usage\") VALUES (@p0_0, @p0_1, @p0_2)", sql);
        Assert.Equal(new UntypedText("2026-10-21T07:00:00Z"), parameters["p0_0"]);
        Assert.Equal(new UntypedText("web-1"), parameters["p0_1"]);
        Assert.Equal(41.5, parameters["p0_2"]);
    }

    [Fact]
    public async Task SeveralPoints_AreSeparateCommands_BecauseCrateTakesOneStatementPerCommand()
    {
        var (client, runner) = Create();
        var points = new[]
        {
            new DataPoint("cpu", new Dictionary<string, object> { ["host"] = "a" }, new Dictionary<string, object> { ["usage"] = 1.0 }),
            new DataPoint("cpu", new Dictionary<string, object> { ["host"] = "b" }, new Dictionary<string, object> { ["usage"] = 2.0 }),
            new DataPoint("mem", new Dictionary<string, object>(), new Dictionary<string, object> { ["free"] = 3L }),
        };
        var result = await client.WriteAsync("crate", points);
        Assert.True(result.Success);
        Assert.Equal(3, runner.Executed.Count());
        Assert.All(runner.Executed, call => Assert.DoesNotContain(";", call.Sql));
        Assert.Contains("\"doc\".\"mem\"", runner.Executed.Last().Sql);
    }

    [Fact]
    public async Task WritingNothing_SendsNothing()
    {
        var (client, runner) = Create();
        var result = await client.WriteAsync("crate", Array.Empty<DataPoint>());
        Assert.True(result.Success);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task ABatch_StopsAtTheFirstFailure_AndReportsIt()
    {
        var runner = new FailingRunner(2);
        var client = new CrateDbClient(new DatabaseConnection("1", "c", "h", 5432, "crate", "") { Kind = DatabaseKind.CrateDb }, runner);
        var points = Enumerable.Range(0, 5).Select(i =>
            new DataPoint("cpu", new Dictionary<string, object>(), new Dictionary<string, object> { ["v"] = (double)i })).ToList();
        var result = await client.WriteAsync("crate", points);
        Assert.False(result.Success);
        Assert.Equal("boom", result.Body);
        Assert.Equal(2, runner.Statements.Count);       // the other three were not sent
    }

    // ------------------------------------------------------------------ server

    [Fact]
    public async Task Ping_ReportsTheCrateDbVersion()
    {
        var (client, runner) = Create(r => r.Answer("version['number']", new[] { "v" }, new object[] { "5.9.2" }));
        var ping = await client.PingAsync();
        Assert.True(ping.Success);
        Assert.Equal("CrateDB 5.9.2", ping.Version);
        Assert.Equal(CrateDbClient.VersionNumberSql, runner.Queried.Single().Sql);
    }

    [Fact]
    public async Task Ping_WithoutAVersion_JustSaysCrateDb()
    {
        var (client, _) = Create();
        Assert.Equal("CrateDB", (await client.PingAsync()).Version);
    }

    [Fact]
    public async Task Ping_FailsQuietly_WhenTheServerIsGone()
    {
        var client = new CrateDbClient(new DatabaseConnection("1", "c", "h", 5432, "crate", "") { Kind = DatabaseKind.CrateDb },
            new FailingRunner(1, new NpgsqlException("connection refused")));
        var ping = await client.PingAsync();
        Assert.False(ping.Success);
        Assert.Equal("unavailable", ping.Version);
    }

    [Fact]
    public async Task Diagnostics_ShowTheNodeAndTheCluster()
    {
        var now = new DateTime(2026, 10, 3, 3, 4, 5, DateTimeKind.Utc);
        var hostUptime = (long)(TimeSpan.FromDays(2) + TimeSpan.FromHours(3) + TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(5)).TotalMilliseconds;
        var (client, _) = Create(r => r
            .Answer("os_info", new[] { "name", "hostname", "version", "build_hash", "host_uptime", "current_time", "processors", "heap_max" },
                new object[] { "node-1", "crate-a", "5.9.2", "abc123", hostUptime, now, 8L, 1073741824L })
            .Answer("master_node", new[] { "name", "master_node" }, new object[] { "prod", "node-1" })
            .Answer("count(*) FROM sys.nodes", new[] { "n" }, new object[] { 3L })
            .Answer("current_user", new[] { "u" }, new object[] { "crate" }));

        var diagnostics = await client.GetDiagnosticsAsync();
        var facts = diagnostics.Properties.ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal("5.9.2", diagnostics.BuildVersion);
        Assert.Equal("localhost", diagnostics.Hostname);
        Assert.Equal(TimeSpan.FromDays(2) + TimeSpan.FromHours(3) + TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(5), diagnostics.Uptime);
        Assert.Equal("2d 3h 4m 5s", facts["Host uptime"]);
        Assert.DoesNotContain("Started", facts.Keys);          // CrateDB does not say when a node started
        Assert.Equal("node-1", facts["Node"]);
        Assert.Equal("crate-a", facts["Host name"]);
        Assert.Equal("prod", facts["Cluster"]);
        Assert.Equal("node-1", facts["Master node"]);
        Assert.Equal("3", facts["Nodes"]);
        Assert.Equal("crate", facts["User"]);
        Assert.Equal("8", facts["Processors"]);
    }

    [Fact]
    public async Task Diagnostics_NeedsANode_ButNotTheExtras()
    {
        var (empty, _) = Create();
        Assert.Null(await empty.GetDiagnosticsAsync());

        var (client, _) = Create(r => r.Answer("os_info", new[] { "name", "hostname", "version", "build_hash", "started", "current_time", "processors", "heap_max" },
            new object[] { "n", "h", "5.9.2", "x", 1_000L, DateTime.UtcNow, 2L, 3L }));
        var diagnostics = await client.GetDiagnosticsAsync();
        Assert.DoesNotContain(diagnostics.Properties, p => p.Key == "Cluster");     // the other queries returned nothing
    }

    [Fact]
    public async Task Diagnostics_LeaveOutWhatTheUserMayNotRead()
    {
        // The first query works; everything after it is refused.
        var runner = new FakeSqlRunner().Answer("os_info", new[] { "name", "hostname", "version", "build_hash", "started", "current_time", "processors", "heap_max" },
            new object[] { "n", "h", "5.9.2", "x", 3_600_000L, DateTime.UtcNow, 2L, 3L });
        var client = new CrateDbClient(new DatabaseConnection("1", "c", "h", 5432, "u", "") { Kind = DatabaseKind.CrateDb }, new RefusingAfterFirst(runner));
        var diagnostics = await client.GetDiagnosticsAsync();
        Assert.Equal("n", diagnostics.Properties.Single(p => p.Key == "Node").Value);
        Assert.DoesNotContain(diagnostics.Properties, p => p.Key is "Cluster" or "Nodes" or "User");
    }

    private sealed class RefusingAfterFirst : ISqlRunner
    {
        private readonly ISqlRunner inner;
        private int calls;
        public RefusingAfterFirst(ISqlRunner inner) => this.inner = inner;

        public Task<IReadOnlyList<ResultSeries>> QueryAsync(string? database, string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default) =>
            ++calls == 1 ? inner.QueryAsync(database, sql, parameters, cancellationToken) : throw new PostgresException("denied", "ERROR", "ERROR", "42501");

        public Task<int> ExecuteAsync(string? database, string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default) =>
            inner.ExecuteAsync(database, sql, parameters, cancellationToken);
    }

    [Fact]
    public async Task Statistics_AreNodes_Tables_Shards_AndHealth()
    {
        var (client, runner) = Create(r => r
            .Answer("heap['used']", new[] { "name" }, new object[] { "node-1" })
            .Answer("FROM sys.shards", new[] { "schema_name", "table_name", "shards" }, new object[] { "doc", "cpu", 4L })
            .Answer("FROM sys.health", new[] { "health" }, new object[] { "GREEN" })
            .Answer("information_schema.tables", new[] { "table_schema" }, new object[] { "doc" }));
        var stats = await client.GetStatsAsync();
        Assert.Equal(new[] { "Nodes", "Tables", "Shards", "Health" }, stats.Custom.Keys);
        Assert.Equal(4, runner.Queried.Count());
        Assert.Contains("\"primary\"", runner.Queried.Single(c => c.Sql.Contains("sys.shards")).Sql);   // a keyword, so quoted
    }

    [Fact]
    public async Task AStatisticThatIsRefused_IsLeftOut_TheRestStay()
    {
        var inner = new FakeSqlRunner()
            .Answer("heap['used']", new[] { "name" }, new object[] { "node-1" })
            .Answer("information_schema.tables", new[] { "table_schema" }, new object[] { "doc" });
        var client = new CrateDbClient(new DatabaseConnection("1", "c", "h", 5432, "u", "") { Kind = DatabaseKind.CrateDb },
            new RefusingMatching(inner, "sys.shards", "sys.health"));
        var stats = await client.GetStatsAsync();
        Assert.Equal(new[] { "Nodes", "Tables" }, stats.Custom.Keys);
    }

    /// <summary>Refuses the queries that mention one of the given words, like a user without the right to read those tables.</summary>
    private sealed class RefusingMatching : ISqlRunner
    {
        private readonly ISqlRunner inner;
        private readonly string[] words;
        public RefusingMatching(ISqlRunner inner, params string[] words) { this.inner = inner; this.words = words; }

        public Task<IReadOnlyList<ResultSeries>> QueryAsync(string? database, string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default) =>
            words.Any(sql.Contains) ? throw new PostgresException("denied", "ERROR", "ERROR", "42501") : inner.QueryAsync(database, sql, parameters, cancellationToken);

        public Task<int> ExecuteAsync(string? database, string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default) =>
            inner.ExecuteAsync(database, sql, parameters, cancellationToken);
    }

    // ------------------------------------------------------------------ running queries

    [Theory]
    [InlineData("3a95a6b6-1f2e-4c1d-9d3a-aaaaaaaaaaaa", 0x3a95a6b6)]
    [InlineData("00000001-0000-0000-0000-000000000000", 1)]
    [InlineData("ffffffff-0000-0000-0000-000000000000", -1)]
    [InlineData("", 0)]
    public void AJobId_BecomesAStableNumber(string id, int expected) => Assert.Equal(expected, CrateDbClient.JobNumber(id));

    [Theory]
    [InlineData(0, "0ms")]
    [InlineData(250, "250ms")]
    [InlineData(1000, "1s")]
    [InlineData(65_000, "1m5s")]
    [InlineData(3_723_000, "1h2m3s")]
    [InlineData(-5000, "0ms")]
    public void Durations_AreWrittenTheWayTheListSortsThem(int milliseconds, string expected)
    {
        var text = CrateDbClient.FormatDuration(TimeSpan.FromMilliseconds(milliseconds));
        Assert.Equal(expected, text);
        if (milliseconds > 0) Assert.InRange(Tickbird.Core.Query.DurationText.ToMilliseconds(text)!.Value, milliseconds - 1000.0, milliseconds);   // whole seconds, rounded down
    }

    [Fact]
    public async Task RunningQueries_AreJobs_WithTheirAge()
    {
        var started = new DateTime(2026, 10, 21, 7, 0, 0, DateTimeKind.Utc);
        var (client, runner) = Create(r => r.Answer("sys.jobs", new[] { "id", "started", "current_time", "stmt" },
            new object[] { "3a95a6b6-1f2e-4c1d-9d3a-aaaaaaaaaaaa", started, started.AddSeconds(65), "SELECT * FROM big" },
            // a server that reports the start time as epoch milliseconds
            new object[] { "00000002-0000-0000-0000-000000000000", new DateTimeOffset(started).ToUnixTimeMilliseconds(), new DateTimeOffset(started).ToUnixTimeMilliseconds() + 2500, "REFRESH TABLE x" }));
        var queries = (await client.GetRunningQueriesAsync()).ToList();
        Assert.Equal(2, queries.Count);
        Assert.Equal(0x3a95a6b6, queries[0].PID);
        Assert.Equal("1m5s", queries[0].Duration);
        Assert.Equal("crate", queries[0].Database);
        Assert.Equal("SELECT * FROM big", queries[0].Query);
        Assert.Equal("2s", queries[1].Duration);
        Assert.Contains("NOT LIKE '%sys.jobs%'", runner.Queried.Single().Sql);      // this very listing is not in the list
    }

    [Fact]
    public async Task AJob_IsKilledById_AfterItWasListed()
    {
        var started = DateTime.UtcNow;
        var (client, runner) = Create(r => r.Answer("sys.jobs", new[] { "id", "started", "current_time", "stmt" },
            new object[] { "3a95a6b6-1f2e-4c1d-9d3a-aaaaaaaaaaaa", started, started, "SELECT 1" }));
        var query = (await client.GetRunningQueriesAsync()).Single();
        var result = await client.KillQueryAsync(query.PID);
        Assert.True(result.Success);
        Assert.Equal("KILL '3a95a6b6-1f2e-4c1d-9d3a-aaaaaaaaaaaa'", runner.Executed.Single().Sql);
    }

    [Fact]
    public async Task AJobThatWasNeverListed_IsNotKilled()
    {
        var (client, runner) = Create();
        var result = await client.KillQueryAsync(12345);
        Assert.False(result.Success);
        Assert.Contains("no longer running", result.Body);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task AJobIdThatIsNotAGuid_IsNeverPutIntoSql()
    {
        var started = DateTime.UtcNow;
        var evil = "1'; DROP TABLE x; --";
        var (client, runner) = Create(r => r.Answer("sys.jobs", new[] { "id", "started", "current_time", "stmt" },
            new object[] { evil, started, started, "SELECT 1" }));
        var query = (await client.GetRunningQueriesAsync()).Single();
        var result = await client.KillQueryAsync(query.PID);
        Assert.False(result.Success);
        Assert.Empty(runner.Executed);
    }

    [Fact]
    public async Task AFreshListing_ForgetsJobsThatHaveEnded()
    {
        var started = DateTime.UtcNow;
        var runner = new FakeSqlRunner().Answer("sys.jobs", new[] { "id", "started", "current_time", "stmt" },
            new object[] { "3a95a6b6-1f2e-4c1d-9d3a-aaaaaaaaaaaa", started, started, "SELECT 1" });
        var client = new CrateDbClient(new DatabaseConnection("1", "c", "h", 5432, "crate", "") { Kind = DatabaseKind.CrateDb }, runner);
        var pid = (await client.GetRunningQueriesAsync()).Single().PID;
        var empty = new FakeSqlRunner();
        var second = new CrateDbClient(client.Connection, empty);
        Assert.Empty(await second.GetRunningQueriesAsync());
        Assert.False((await second.KillQueryAsync(pid)).Success);
    }

    // ------------------------------------------------------------------ users and privileges

    [Fact]
    public async Task Users_AreListedFromSys()
    {
        var (client, runner) = Create(r => r.Answer("sys.users", new[] { "name", "superuser" },
            new object[] { "crate", true }, new object[] { "reader", false }));
        var users = (await client.GetUsersAsync()).ToList();
        Assert.Equal(new[] { "crate", "reader" }, users.Select(u => u.Name));
        Assert.Equal(new[] { true, false }, users.Select(u => u.IsAdmin));
        Assert.Equal("crate", runner.Queried.Single().Database);
    }

    [Fact]
    public async Task AUser_IsCreated_WithThePasswordAsAProperty()
    {
        var (client, runner) = Create();
        Assert.True((await client.CreateUserAsync("app user", "se'cret\\1", false)).Success);
        Assert.Equal("CREATE USER \"app user\" WITH (password = 'se''cret\\1')", runner.Executed.Single().Sql);
    }

    [Fact]
    public async Task AUserWithoutAPassword_IsCreatedWithoutOne()
    {
        var (client, runner) = Create();
        await client.CreateUserAsync("guest", "", false);
        Assert.Equal("CREATE USER \"guest\"", runner.Executed.Single().Sql);
    }

    [Fact]
    public async Task ASuperuser_CannotBeCreatedWithSql()
    {
        var (client, runner) = Create();
        var result = await client.CreateUserAsync("root", "pw", true);
        Assert.False(result.Success);
        Assert.Contains("superuser", result.Body);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task APassword_CannotEndTheStringEarly()
    {
        var (client, runner) = Create();
        await client.CreateUserAsync("u", "x'); DROP USER crate; --", false);
        Assert.Equal("CREATE USER \"u\" WITH (password = 'x''); DROP USER crate; --')", runner.Executed.Single().Sql);
    }

    [Fact]
    public async Task AUser_IsDropped_AndItsPasswordChanged()
    {
        var (client, runner) = Create();
        await client.DropUserAsync("reader");
        await client.SetPasswordAsync("reader", "new'pw");
        await client.SetPasswordAsync("reader", "");
        var statements = runner.Executed.Select(c => c.Sql).ToList();
        Assert.Equal("DROP USER \"reader\"", statements[0]);
        Assert.Equal("ALTER USER \"reader\" SET (password = 'new''pw')", statements[1]);
        Assert.Equal("ALTER USER \"reader\" SET (password = NULL)", statements[2]);
    }

    [Fact]
    public async Task AnExistingUserError_IsAFailedResult_WithTheServersMessage()
    {
        var client = new CrateDbClient(new DatabaseConnection("1", "c", "h", 5432, "crate", "") { Kind = DatabaseKind.CrateDb },
            new FailingRunner(1, new PostgresException("User 'a' already exists", "ERROR", "ERROR", "XX000")));
        var result = await client.CreateUserAsync("a", "b", false);
        Assert.False(result.Success);
        Assert.Equal("User 'a' already exists", result.Body);
    }

    [Fact]
    public async Task Administrators_CannotBeChanged()
    {
        var (client, _) = Create();
        await Assert.ThrowsAsync<NotSupportedException>(() => client.GrantAdministratorAsync("u"));
        await Assert.ThrowsAsync<NotSupportedException>(() => client.RevokeAdministratorAsync("u"));
    }

    [Theory]
    [InlineData(new[] { "DQL" }, DatabasePrivileges.Read)]
    [InlineData(new[] { "DML" }, DatabasePrivileges.Write)]
    [InlineData(new[] { "DQL", "DML" }, DatabasePrivileges.All)]
    [InlineData(new[] { "DQL", "DML", "DDL" }, DatabasePrivileges.All)]
    [InlineData(new[] { "AL" }, DatabasePrivileges.All)]
    [InlineData(new[] { "dql" }, DatabasePrivileges.Read)]
    public async Task Privileges_AreSimplifiedToReadWriteAll(string[] types, DatabasePrivileges expected)
    {
        var (client, runner) = Create(r => r.Answer("sys.privileges", new[] { "type" }, types.Select(t => new object[] { t }).ToArray()));
        var grant = Assert.Single(await client.GetPrivilegesAsync("reader"));
        Assert.Equal("crate", grant.Database);
        Assert.Equal(expected, grant.Privilege);
        Assert.Equal("reader", runner.Queried.Single().Parameters!["user"]);
    }

    [Fact]
    public async Task OnlyDdl_Or_NoPrivileges_IsNoGrant()
    {
        var (ddl, _) = Create(r => r.Answer("sys.privileges", new[] { "type" }, new object[] { "DDL" }));
        Assert.Empty(await ddl.GetPrivilegesAsync("u"));
        var (none, _) = Create();
        Assert.Empty(await none.GetPrivilegesAsync("u"));
    }

    [Fact]
    public async Task OnlyGrantedClusterPrivileges_Count()
    {
        var (client, runner) = Create();
        await client.GetPrivilegesAsync("u");
        var sql = runner.Queried.Single().Sql;
        Assert.Contains("class = 'CLUSTER'", sql);
        Assert.Contains("state = 'GRANT'", sql);        // a denied privilege is not a grant
    }

    [Fact]
    public void PrivilegeStatements_FirstTakeTheOldOnesAway()
    {
        Assert.Equal(new[] { "REVOKE DQL, DML, DDL FROM \"u\"", "GRANT DQL TO \"u\"" },
            CrateDbClient.PrivilegeStatements("u", DatabasePrivileges.Read));
        Assert.Equal(new[] { "REVOKE DQL, DML, DDL FROM \"u\"", "GRANT DML TO \"u\"" },
            CrateDbClient.PrivilegeStatements("u", DatabasePrivileges.Write));
        Assert.Equal(new[] { "REVOKE DQL, DML, DDL FROM \"u\"", "GRANT DQL, DML, DDL TO \"u\"" },
            CrateDbClient.PrivilegeStatements("u", DatabasePrivileges.All));
        Assert.Equal(new[] { "REVOKE DQL, DML, DDL FROM \"u\"" }, CrateDbClient.PrivilegeStatements("u", DatabasePrivileges.None));
        Assert.Equal("REVOKE DQL, DML, DDL FROM \"a\"\"b\"", CrateDbClient.PrivilegeStatements("a\"b", DatabasePrivileges.None).First());
    }

    [Fact]
    public async Task Granting_RunsEachStatementOnItsOwn()
    {
        var (client, runner) = Create();
        Assert.True((await client.GrantPrivilegeAsync("u", DatabasePrivileges.All, "crate")).Success);
        Assert.Equal(new[] { "REVOKE DQL, DML, DDL FROM \"u\"", "GRANT DQL, DML, DDL TO \"u\"" }, runner.Executed.Select(c => c.Sql));
        Assert.All(runner.Executed, c => Assert.DoesNotContain(";", c.Sql));
    }

    [Fact]
    public async Task Revoking_OnlyTakesAway()
    {
        var (client, runner) = Create();
        Assert.True((await client.RevokePrivilegeAsync("u", DatabasePrivileges.Read, "crate")).Success);
        Assert.Equal(new[] { "REVOKE DQL, DML, DDL FROM \"u\"" }, runner.Executed.Select(c => c.Sql));
    }

    [Fact]
    public async Task AFailedRevoke_MeansNothingIsGranted()
    {
        var runner = new FailingRunner(1);
        var client = new CrateDbClient(new DatabaseConnection("1", "c", "h", 5432, "crate", "") { Kind = DatabaseKind.CrateDb }, runner);
        var result = await client.GrantPrivilegeAsync("u", DatabasePrivileges.Read, "crate");
        Assert.False(result.Success);
        Assert.Single(runner.Statements);       // the GRANT was not tried after the REVOKE failed
    }

    // ------------------------------------------------------------------ not in CrateDB

    [Fact]
    public async Task RetentionContinuousQueriesAndBackfill_AreNotSupported()
    {
        var (client, runner) = Create();
        await Assert.ThrowsAsync<NotSupportedException>(() => client.GetRetentionPoliciesAsync("crate"));
        await Assert.ThrowsAsync<NotSupportedException>(() => client.CreateRetentionPolicyAsync("crate", "t", "7d", 1));
        await Assert.ThrowsAsync<NotSupportedException>(() => client.AlterRetentionPolicyAsync("crate", "t", "7d", 1));
        await Assert.ThrowsAsync<NotSupportedException>(() => client.DropRetentionPolicyAsync("crate", "t"));
        await Assert.ThrowsAsync<NotSupportedException>(() => client.GetContinousQueriesAsync("crate"));
        await Assert.ThrowsAsync<NotSupportedException>(() => client.CreateContinuousQueryAsync(new ContinuousQueryParams()));
        await Assert.ThrowsAsync<NotSupportedException>(() => client.DropContinuousQueryAsync("crate", "q"));
        await Assert.ThrowsAsync<NotSupportedException>(() => client.BackfillAsync("crate", new BackfillParams()));
        Assert.Empty(runner.Calls);
    }

    // ------------------------------------------------------------------ queries

    [Fact]
    public async Task AUsersQuery_RunsAsWritten_OnTheOneDatabase()
    {
        var (client, runner) = Create(r => r.Answer("SELECT 1", new[] { "x" }, new object[] { 1L }));
        var result = (await client.QueryAsync("crate", "SELECT 1")).Single();
        Assert.Equal(new[] { "x" }, result.Columns);
        Assert.Equal("SELECT 1", runner.Queried.Single().Sql);
        var cancellable = (await client.QueryAsync("crate", "SELECT 1", CancellationToken.None)).Single();
        Assert.Single(cancellable.Values);
    }
}

public class CrateDbProfileTests
{
    private static DatabaseProfile Profile => DatabaseProfiles.Get(DatabaseKind.CrateDb);

    [Fact]
    public void CrateDb_IsRegistered_WithItsDefaults()
    {
        Assert.Same(CrateDbClient.Profile, Profile);
        Assert.Equal("CrateDB", Profile.DisplayName);
        Assert.Equal(5432, Profile.DefaultPort);
        Assert.True(Profile.IsSql);
        Assert.Equal(AuthenticationStyle.UsernamePassword, Profile.Authentication);
        Assert.Equal("crate", Profile.DefaultUsername);
        Assert.Equal("CrateDB Version", Profile.VersionLabel);
        Assert.False(Profile.UsesDatabaseName);
        Assert.False(Profile.RetentionPerTable);
    }

    [Fact]
    public void TheFactory_CreatesACrateDbClient_FromTheKind()
    {
        var connection = new DatabaseConnection("1", "c", "h", 5432, "crate", "") { Kind = DatabaseKind.CrateDb };
        Assert.IsType<CrateDbClient>(DatabaseClientFactory.Create(connection));
        Assert.Same(Profile, connection.Profile);
    }

    [Fact]
    public void TheKind_IsSaved_AndOldFilesAreUnaffected()
    {
        var connection = new DatabaseConnection("1", "c", "h", 5432, "crate", "") { Kind = DatabaseKind.CrateDb };
        var copy = Newtonsoft.Json.JsonConvert.DeserializeObject<DatabaseConnection>(Newtonsoft.Json.JsonConvert.SerializeObject(connection))!;
        Assert.Equal(DatabaseKind.CrateDb, copy.Kind);
        Assert.Equal(InfluxDbServerVersion.InfluxDb1, copy.ServerVersion);     // the old setting is not touched by a new kind
        Assert.Equal(DatabaseKind.InfluxDb1, Newtonsoft.Json.JsonConvert.DeserializeObject<DatabaseConnection>("{\"Id\":\"1\",\"Name\":\"a\",\"Host\":\"h\",\"Port\":8086}")!.Kind);
    }

    [Fact]
    public void WhatCrateDbCanDo_IsDeclaredHonestly()
    {
        foreach (var can in new[]
        {
            DatabaseCapabilities.Users, DatabaseCapabilities.Privileges, DatabaseCapabilities.RunningQueries, DatabaseCapabilities.Statistics,
            DatabaseCapabilities.Diagnostics, DatabaseCapabilities.TagKeys, DatabaseCapabilities.TagValues, DatabaseCapabilities.FieldKeys,
            DatabaseCapabilities.Series, DatabaseCapabilities.DropSeries, DatabaseCapabilities.DropMeasurement, DatabaseCapabilities.WriteData
        }) Assert.True(Profile.Supports(can), can.ToString());

        foreach (var cannot in new[]
        {
            DatabaseCapabilities.CreateDatabase, DatabaseCapabilities.DropDatabase, DatabaseCapabilities.RetentionPolicies,
            DatabaseCapabilities.ContinuousQueries, DatabaseCapabilities.Backfill, DatabaseCapabilities.AdministratorRights
        }) Assert.False(Profile.Supports(cannot), cannot.ToString());
    }

    [Fact]
    public void TheWordsOfTheUi_AreCrateDbs()
    {
        var terms = Profile.Terms;
        Assert.Equal("Table", terms.Measurement);
        Assert.Equal("Text Columns", terms.TagKeys);
        Assert.Equal("Columns", terms.FieldKeys);
        Assert.Equal("Partitions", terms.Series);
        Assert.Equal("Delete All Rows", terms.DropSeries);
        Assert.Equal("Users", terms.Users);
    }

    [Fact]
    public void TheCommands_FollowCrateDb()
    {
        var onServer = CommandAvailability.For(NodeKind.Connection, "srv", Profile, false, false);
        Assert.True(onServer.ShowUsers && onServer.ShowStats && onServer.ShowDiagnostics && onServer.ShowQueries);
        Assert.False(onServer.CreateDatabase || onServer.ShowPolicies);

        var onDatabase = CommandAvailability.For(NodeKind.Database, "crate", Profile, false, false);
        Assert.False(onDatabase.DropDatabase || onDatabase.ContinuousQueries || onDatabase.Backfill);
        Assert.True(onDatabase.NewQuery);

        var onTable = CommandAvailability.For(NodeKind.Measurement, "cpu", Profile, false, false);
        Assert.True(onTable.TagKeys && onTable.TagValues && onTable.FieldKeys && onTable.ShowSeries && onTable.DropSeries && onTable.DropMeasurement);
    }

    [Fact]
    public void TheStarterQuery_NamesTheTable_InTheRightSchema()
    {
        Assert.Equal("SELECT * FROM \"doc\".\"cpu\" LIMIT 100", Profile.SampleQuery("cpu"));
        Assert.Equal("SELECT * FROM \"metrics\".\"cpu\" LIMIT 100", Profile.SampleQuery("metrics.cpu"));
        Assert.Equal(Profile.SampleQuery("cpu"), Tickbird.Core.Query.QueryText.SampleQuery(Profile, "cpu"));
    }

    [Fact]
    public void TheEditorOffersCrateDbFunctions_AndTheirDetails()
    {
        var extras = Profile.CompletionExtras.ToDictionary(e => e.Text, e => e);
        Assert.True(extras["date_bin()"].IsFunction);
        Assert.False(extras["sys.nodes"].IsFunction);
        Assert.All(Profile.CompletionExtras, e => Assert.False(string.IsNullOrWhiteSpace(e.Detail)));
        Assert.Equal(Profile.CompletionExtras.Count, Profile.CompletionExtras.Select(e => e.Text).Distinct().Count());
    }

    [Theory]
    [InlineData(5432, null)]
    [InlineData(5433, null)]
    [InlineData(4200, "HTTP port")]
    public void TheHttpPort_IsRefused_BecauseItDoesNotSpeakThePostgresProtocol(int port, string? expected)
    {
        var problem = Profile.Validate(new DatabaseConnection("1", "c", "h", (ushort)port, "crate", "") { Kind = DatabaseKind.CrateDb });
        if (expected == null) Assert.Null(problem);
        else Assert.Contains(expected, problem);
    }

    [Fact]
    public void ANameAndAHost_AreAlwaysRequired()
    {
        Assert.Equal("Connection name cannot be blank.", Profile.Validate(new DatabaseConnection("1", " ", "h", 5432, "crate", "")));
        Assert.Equal("Host cannot be blank.", Profile.Validate(new DatabaseConnection("1", "c", "", 5432, "crate", "")));
        foreach (var other in DatabaseProfiles.All.Where(p => p.ValidateConnection == null))
            Assert.Null(other.Validate(new DatabaseConnection("1", "c", "h", other.DefaultPort, "u", "p") { Kind = other.Kind }));
    }

    [Fact]
    public void TheDialogNote_SaysWhichPortSpeaksWhichProtocol()
    {
        Assert.Contains("PostgreSQL protocol", Profile.ConnectionHint);
        Assert.Contains("4200", Profile.ConnectionHint);
        Assert.Null(DatabaseProfiles.Get(DatabaseKind.InfluxDb1).ConnectionHint);
    }

    [Fact]
    public void OtherDatabases_KeepTheirDefaults()
    {
        foreach (var kind in new[] { DatabaseKind.InfluxDb1, DatabaseKind.InfluxDb3, DatabaseKind.TimescaleDb })
        {
            var profile = DatabaseProfiles.Get(kind);
            Assert.True(profile.UsesDatabaseName, kind.ToString());
            Assert.Null(profile.DefaultUsername);
            Assert.Equal("Database", profile.Terms.Database);
        }
        Assert.True(DatabaseProfiles.Get(DatabaseKind.InfluxDb1).Supports(DatabaseCapabilities.AdministratorRights));
        Assert.True(DatabaseProfiles.Get(DatabaseKind.TimescaleDb).Supports(DatabaseCapabilities.AdministratorRights));
        Assert.False(DatabaseProfiles.Get(DatabaseKind.InfluxDb3).Supports(DatabaseCapabilities.AdministratorRights));
    }

    [Fact]
    public void TheNewKind_DoesNotChangeTheNumbersOfTheOldOnes()
    {
        Assert.Equal(0, (int)DatabaseKind.InfluxDb1);
        Assert.Equal(3, (int)DatabaseKind.InfluxDb3);
        Assert.Equal(10, (int)DatabaseKind.TimescaleDb);
        Assert.Equal(11, (int)DatabaseKind.CrateDb);
    }
}
