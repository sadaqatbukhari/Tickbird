using System.Globalization;
using Npgsql;
using Tickbird.Core.Data.Sql;
using Tickbird.Core.Providers;

namespace Tickbird.Core.Data;

/// <summary>
/// TimescaleDB: PostgreSQL with hypertables. It maps onto the same concepts as InfluxDB so every view works:
/// a hypertable is a measurement, its text columns are tags and its other columns fields, a continuous aggregate is
/// a continuous query, a retention policy removes old chunks, and a role is a user. Chunks are the "series".
/// </summary>
public sealed class TimescaleDbClient : PostgresClientBase
{
    private const DatabaseCapabilities Capabilities =
        DatabaseCapabilities.CreateDatabase | DatabaseCapabilities.DropDatabase | DatabaseCapabilities.Users
        | DatabaseCapabilities.Privileges | DatabaseCapabilities.RetentionPolicies | DatabaseCapabilities.ContinuousQueries
        | DatabaseCapabilities.Backfill | DatabaseCapabilities.RunningQueries | DatabaseCapabilities.Statistics
        | DatabaseCapabilities.Diagnostics | DatabaseCapabilities.TagKeys | DatabaseCapabilities.TagValues
        | DatabaseCapabilities.FieldKeys | DatabaseCapabilities.Series | DatabaseCapabilities.DropSeries
        | DatabaseCapabilities.DropMeasurement | DatabaseCapabilities.WriteData | DatabaseCapabilities.AdministratorRights;

    /// <summary>How TimescaleDB looks in the app.</summary>
    public static readonly DatabaseProfile Profile = new(DatabaseKind.TimescaleDb, "TimescaleDB", 5432, isSql: true,
        AuthenticationStyle.UsernamePassword, Capabilities,
        new DatabaseTerms("Table", "Tables", "Text Columns", "Column Values", "Columns", "Chunks", "Truncate Table",
            "Continuous Aggregate", "Retention Policy", "Role", "Continuous Aggregates", "Retention Policies", "Roles"),
        connection => new TimescaleDbClient(connection))
    {
        RetentionPerTable = true,
        ContinuousQueryWritesToDestination = false,
        DefaultDatabase = "postgres",
        SystemDatabases = new[] { "postgres", "template0", "template1" },
        VersionLabel = "TimescaleDB Version",
        SampleQuery = table => $"SELECT * FROM {PostgresSql.Table(table)} ORDER BY 1 DESC LIMIT 100",
        CompletionExtras = new (string, string, bool)[]
        {
            ("time_bucket()", "TimescaleDB: group rows into time buckets", true),
            ("time_bucket_gapfill()", "TimescaleDB: time buckets with empty buckets filled in", true),
            ("first()", "TimescaleDB: value at the earliest time", true),
            ("last()", "TimescaleDB: value at the latest time", true),
            ("locf()", "TimescaleDB: carry the last value forward in a gapfill", true),
            ("interpolate()", "TimescaleDB: interpolate values in a gapfill", true),
            ("histogram()", "TimescaleDB: histogram of a column", true),
            ("approximate_row_count()", "TimescaleDB: fast row count of a table", true),
            ("hypertable_size()", "TimescaleDB: disk space of a hypertable", true),
            ("show_chunks()", "TimescaleDB: the chunks of a hypertable", true),
            ("drop_chunks()", "TimescaleDB: delete old chunks", true),
            ("add_retention_policy()", "TimescaleDB: delete old data automatically", true),
            ("add_compression_policy()", "TimescaleDB: compress old data automatically", true),
            ("refresh_continuous_aggregate()", "TimescaleDB: refresh a continuous aggregate", true),
            ("create_hypertable()", "TimescaleDB: turn a table into a hypertable", true),
            ("date_trunc()", "Cut a time down to a unit", true),
            ("generate_series()", "A series of numbers or times", true),
            ("percentile_cont()", "Continuous percentile", true),
            ("now()", "The current time", true),
            ("hypertable", "TimescaleDB", false),
            ("continuous", "TimescaleDB", false),
        }
    };

    public TimescaleDbClient(DatabaseConnection connection, ISqlRunner? runner = null) : base(connection, runner)
    {
    }

    protected override string ProductName => "TimescaleDB";

    protected override string HiddenSchemas =>
        base.HiddenSchemas + " AND n.nspname NOT LIKE '\\_timescaledb%' AND n.nspname NOT LIKE 'timescaledb\\_%'";

    // ------------------------------------------------------------------ server

    public const string ExtensionVersionSql = "SELECT extversion FROM pg_extension WHERE extname = 'timescaledb'";

    protected override async Task<string> GetVersionTextAsync()
    {
        var postgres = await base.GetVersionTextAsync().ConfigureAwait(false);
        var extension = Rows(await Sql.QueryAsync(null, ExtensionVersionSql).ConfigureAwait(false)).Select(r => Text(r, 0)).FirstOrDefault();
        return string.IsNullOrEmpty(extension) ? postgres + " (TimescaleDB extension not installed)" : $"{extension} on {postgres}";
    }

    protected override async Task<IEnumerable<KeyValuePair<string, string>>> GetExtraDiagnosticsAsync()
    {
        var rows = new List<KeyValuePair<string, string>>();
        try
        {
            var version = Rows(await Sql.QueryAsync(null, ExtensionVersionSql).ConfigureAwait(false)).Select(r => Text(r, 0)).FirstOrDefault();
            rows.Add(new("TimescaleDB version", string.IsNullOrEmpty(version) ? "not installed" : version));
            if (string.IsNullOrEmpty(version)) return rows;
            var count = Rows(await Sql.QueryAsync(null, "SELECT count(*) FROM timescaledb_information.hypertables").ConfigureAwait(false)).Select(r => Text(r, 0)).FirstOrDefault();
            rows.Add(new("Hypertables", count ?? "0"));
            var license = Rows(await Sql.QueryAsync(null, "SELECT current_setting('timescaledb.license', true)").ConfigureAwait(false)).Select(r => Text(r, 0)).FirstOrDefault();
            if (!string.IsNullOrEmpty(license)) rows.Add(new("License", license));
        }
        catch (PostgresException) { /* the extension is not available to this account */ }
        return rows;
    }

    protected override IEnumerable<(string Name, string Sql)> StatisticQueries => base.StatisticQueries
        // TimescaleDB's own catalog tables are not user tables worth listing.
        .Select(q => q.Name == "Tables" ? (q.Name, q.Sql.Replace("FROM pg_stat_user_tables", "FROM pg_stat_user_tables WHERE schemaname NOT LIKE '\\_timescaledb%'")) : q)
        .Concat(new[]
    {
        ("Hypertables", "SELECT hypertable_schema, hypertable_name, num_dimensions, num_chunks, compression_enabled, " +
                        "pg_size_pretty(hypertable_size(format('%I.%I', hypertable_schema, hypertable_name)::regclass)) AS total_size " +
                        "FROM timescaledb_information.hypertables ORDER BY 1, 2"),
        ("Background jobs", "SELECT j.job_id, j.proc_name, j.hypertable_schema, j.hypertable_name, s.last_run_status, " +
                            "s.last_run_started_at, s.next_start, s.total_runs, s.total_failures " +
                            "FROM timescaledb_information.jobs j LEFT JOIN timescaledb_information.job_stats s USING (job_id) ORDER BY j.job_id"),
    });

    // ------------------------------------------------------------------ databases

    /// <summary>Creates the database and installs the TimescaleDB extension in it, where this account may.</summary>
    public override async Task<CommandResult> CreateDatabaseAsync(string database)
    {
        var created = await base.CreateDatabaseAsync(database).ConfigureAwait(false);
        if (!created.Success) return created;
        var extension = await ExecuteAsync(database, "CREATE EXTENSION IF NOT EXISTS timescaledb").ConfigureAwait(false);
        return extension.Success
            ? Ok("Database created with the TimescaleDB extension.")
            : Ok("Database created, but the TimescaleDB extension could not be installed: " + extension.Body);
    }

    // ------------------------------------------------------------------ chunks as series

    public const string ChunksSql =
        "SELECT chunk_schema, chunk_name, range_start, range_end, is_compressed FROM timescaledb_information.chunks " +
        "WHERE hypertable_schema = @schema AND hypertable_name = @table ORDER BY range_start DESC, chunk_name LIMIT 1000";

    private static string Stamp(object? value) =>
        value is DateTime dateTime ? dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

    /// <summary>The chunks of a hypertable as series keys: <c>cpu,chunk=_hyper_1_3_chunk,range_start=...,range_end=...,compressed=false</c>.</summary>
    public override async Task<IEnumerable<string>> GetSeriesNamesAsync(string database, string measurement = null)
    {
        if (string.IsNullOrWhiteSpace(measurement)) return Array.Empty<string>();
        var (schema, table) = PostgresSql.SplitQualified(measurement);
        var chunks = Rows(await Sql.QueryAsync(database, ChunksSql, Args(("schema", schema), ("table", table))).ConfigureAwait(false))
            .Select(r => $"{measurement},chunk={Text(r, 1)},range_start={Stamp(r[2])},range_end={Stamp(r[3])},compressed={(r.Count > 4 && r[4] is true ? "true" : "false")}")
            .ToList();
        // A plain table has no chunks: show its distinct tag combinations instead.
        return chunks.Count > 0 ? chunks : await base.GetSeriesNamesAsync(database, measurement).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ retention policies

    public const string RetentionPoliciesSql =
        "SELECT j.hypertable_schema, j.hypertable_name, j.config ->> 'drop_after' AS drop_after, " +
        "(SELECT coalesce(d.time_interval::text, d.integer_interval::text) FROM timescaledb_information.dimensions d " +
        "WHERE d.hypertable_schema = j.hypertable_schema AND d.hypertable_name = j.hypertable_name AND d.dimension_number = 1) AS chunk_interval " +
        "FROM timescaledb_information.jobs j WHERE j.proc_name = 'policy_retention' ORDER BY 1, 2";

    /// <summary>One policy per hypertable: its name is the hypertable, the duration is how long data is kept.</summary>
    public override async Task<IEnumerable<RetentionPolicy>> GetRetentionPoliciesAsync(string database) =>
        Rows(await Sql.QueryAsync(database, RetentionPoliciesSql).ConfigureAwait(false)).Select(r => new RetentionPolicy
        {
            Name = PostgresSql.DisplayName(Text(r, 0), Text(r, 1)),
            Database = database,
            Duration = Text(r, 2),
            ShardGroupDuration = Text(r, 3),
            ReplicationCopies = 1,
            Default = false
        }).ToList();

    /// <summary>Statements that give a hypertable a retention policy (any earlier policy is replaced).</summary>
    public static string RetentionStatement(string hypertable, string duration) =>
        $"SELECT remove_retention_policy(@table::regclass, if_exists => true);\n" +
        $"SELECT add_retention_policy(@table::regclass, drop_after => {PostgresSql.IntervalLiteral(duration)})";

    public override async Task<CommandResult> CreateRetentionPolicyAsync(string database, string policyName, string duration, int replication, bool isDefault = false)
    {
        try { return await ExecuteAsync(database, RetentionStatement(policyName, duration), Args(("table", PostgresSql.Table(policyName)))).ConfigureAwait(false); }
        catch (FormatException ex) { return Failed(ex.Message); }
    }

    public override Task<CommandResult> AlterRetentionPolicyAsync(string database, string policyName, string duration, int replication, bool isDefault = false) =>
        CreateRetentionPolicyAsync(database, policyName, duration, replication, isDefault);

    public override async Task<CommandResult> DropRetentionPolicyAsync(string database, string policyName) =>
        await ExecuteAsync(database, "SELECT remove_retention_policy(@table::regclass, if_exists => true)",
            Args(("table", PostgresSql.Table(policyName)))).ConfigureAwait(false);

    // ------------------------------------------------------------------ continuous aggregates (continuous queries)

    public const string ContinuousAggregatesSql =
        "SELECT view_schema, view_name, view_definition FROM timescaledb_information.continuous_aggregates ORDER BY 1, 2";

    public override async Task<IEnumerable<ContinuousQuery>> GetContinousQueriesAsync(string database) =>
        Rows(await Sql.QueryAsync(database, ContinuousAggregatesSql).ConfigureAwait(false))
            .Select(r => new ContinuousQuery(PostgresSql.DisplayName(Text(r, 0), Text(r, 1)),
                $"CREATE MATERIALIZED VIEW {PostgresSql.Table(PostgresSql.DisplayName(Text(r, 0), Text(r, 1)))}\nWITH (timescaledb.continuous) AS\n{Text(r, 2).Trim()}")).ToList();

    public const string TimeColumnSql =
        "SELECT column_name FROM timescaledb_information.dimensions WHERE hypertable_schema = @schema AND hypertable_name = @table AND dimension_number = 1";

    private async Task<string> TimeColumnOfAsync(string database, string table)
    {
        var (schema, name) = PostgresSql.SplitQualified(table);
        return Rows(await Sql.QueryAsync(database, TimeColumnSql, Args(("schema", schema), ("table", name))).ConfigureAwait(false))
                   .Select(r => Text(r, 0)).FirstOrDefault() ?? TimeColumn;
    }

    /// <summary>
    /// The SELECT that downsamples a table: time buckets of <paramref name="interval"/>, the tag columns, then the
    /// aggregate expressions the user wrote (for example <c>avg("usage") AS "usage"</c>).
    /// </summary>
    public static string BucketSelect(string source, string timeColumn, string interval, IEnumerable<string>? tags,
        IEnumerable<string> aggregates, string? where = null)
    {
        var tagList = (tags ?? Enumerable.Empty<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => PostgresSql.Identifier(t.Trim())).ToList();
        var expressions = aggregates.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList();
        if (expressions.Count == 0) throw new FormatException("Enter at least one aggregate, for example avg(\"value\") AS \"value\".");
        var select = new List<string> { $"time_bucket({PostgresSql.IntervalLiteral(interval)}, {PostgresSql.Identifier(timeColumn)}) AS {PostgresSql.Identifier(TimeColumnAlias)}" };
        select.AddRange(tagList);
        select.AddRange(expressions);
        var group = new List<string> { "1" };
        group.AddRange(tagList);
        return $"SELECT {string.Join(", ", select)}\nFROM {PostgresSql.Table(source)}" +
               (string.IsNullOrWhiteSpace(where) ? "" : $"\nWHERE {where}") +
               $"\nGROUP BY {string.Join(", ", group)}";
    }

    private const string TimeColumnAlias = "time";

    /// <summary>Creates the continuous aggregate and a refresh policy that keeps it up to date every interval.</summary>
    public override async Task<CommandResult> CreateContinuousQueryAsync(ContinuousQueryParams p)
    {
        try
        {
            var timeColumn = await TimeColumnOfAsync(p.Database, p.Source).ConfigureAwait(false);
            var select = BucketSelect(p.Source, timeColumn, p.Interval, p.Tags, p.SubQueries ?? Enumerable.Empty<string>());
            var view = PostgresSql.Table(p.Name);
            // A continuous aggregate cannot be created inside a transaction block, so each statement runs on its own.
            var created = await ExecuteAsync(p.Database, $"CREATE MATERIALIZED VIEW {view} WITH (timescaledb.continuous) AS\n{select}\nWITH NO DATA").ConfigureAwait(false);
            if (!created.Success) return created;
            var interval = PostgresSql.IntervalLiteral(p.Interval);
            var policy = await ExecuteAsync(p.Database,
                "SELECT add_continuous_aggregate_policy(@view::regclass, start_offset => " + interval + " * 3, end_offset => " + interval +
                ", schedule_interval => " + interval + ")", Args(("view", view))).ConfigureAwait(false);
            return policy.Success ? Ok($"Created {p.Name} and a refresh policy every {p.Interval}.") : Failed("The aggregate was created, but its refresh policy failed: " + policy.Body);
        }
        catch (FormatException ex) { return Failed(ex.Message); }
    }

    public override async Task<CommandResult> DropContinuousQueryAsync(string database, string cqName) =>
        await ExecuteAsync(database, "DROP MATERIALIZED VIEW IF EXISTS " + PostgresSql.Table(cqName)).ConfigureAwait(false);

    // ------------------------------------------------------------------ backfill

    private static string Timestamp(DateTime value) =>
        PostgresSql.Literal(DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)) + "::timestamptz";

    /// <summary>Downsamples existing data of a time range into a table: it is created when missing, otherwise rows are added.</summary>
    public override async Task<CommandResult> BackfillAsync(string database, BackfillParams p)
    {
        try
        {
            var timeColumn = await TimeColumnOfAsync(database, p.Source).ConfigureAwait(false);
            var time = PostgresSql.Identifier(timeColumn);
            var where = $"{time} >= {Timestamp(p.FromTime)} AND {time} < {Timestamp(p.ToTime)}" +
                        string.Concat((p.Filters ?? new List<string>()).Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => $" AND ({f.Trim()})"));
            var select = BucketSelect(p.Source, timeColumn, p.Interval, p.Tags, p.SubQueries ?? Enumerable.Empty<string>(), where);
            var exists = Rows(await Sql.QueryAsync(database, "SELECT to_regclass(@name) IS NOT NULL", Args(("name", PostgresSql.Table(p.Destination)))).ConfigureAwait(false))
                .Select(r => r[0] is true).FirstOrDefault();
            return await ExecuteAsync(database, exists
                ? $"INSERT INTO {PostgresSql.Table(p.Destination)}\n{select}"
                : $"CREATE TABLE {PostgresSql.Table(p.Destination)} AS\n{select}").ConfigureAwait(false);
        }
        catch (FormatException ex) { return Failed(ex.Message); }
    }
}
