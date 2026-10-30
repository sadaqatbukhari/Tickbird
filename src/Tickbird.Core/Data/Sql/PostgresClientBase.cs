using System.Globalization;
using System.Net;
using Npgsql;

namespace Tickbird.Core.Data.Sql;

/// <summary>
/// The part of a client that every database speaking the PostgreSQL protocol shares (PostgreSQL, TimescaleDB, and
/// later CrateDB and QuestDB): listing databases and tables, running queries, columns as tags and fields, users and
/// roles, running queries, diagnostics and statistics. A concrete client adds what is special to its database by
/// overriding the <c>virtual</c> members, and writes its own catalog queries where the generic ones do not fit.
/// </summary>
public abstract class PostgresClientBase : DatabaseClient
{
    /// <summary>The most distinct values listed for one tag column.</summary>
    public const int MaxTagValues = 1000;

    /// <summary>The most series (distinct tag combinations) listed for one table.</summary>
    public const int MaxSeries = 1000;

    protected PostgresClientBase(DatabaseConnection connection, ISqlRunner? runner = null) : base(connection)
    {
        Sql = runner ?? new NpgsqlRunner(connection, MaintenanceDatabase);
    }

    /// <summary>Runs the SQL (a fake in tests).</summary>
    protected ISqlRunner Sql { get; }

    /// <summary>A database that always exists, used to connect when no database is chosen and to create or drop databases.</summary>
    protected virtual string MaintenanceDatabase => "postgres";

    /// <summary>
    /// The database to connect to for server-wide work (listing databases, users, creating and dropping databases):
    /// the connection's own database if it names one, so accounts that cannot use "postgres" still work.
    /// <paramref name="avoid"/> is a database that cannot be used, such as the one being dropped.
    /// </summary>
    protected string CatalogDatabase(string? avoid = null)
    {
        var database = string.IsNullOrWhiteSpace(Connection.Database) ? MaintenanceDatabase : Connection.Database;
        if (string.Equals(database, avoid, StringComparison.Ordinal)) database = MaintenanceDatabase;
        if (string.Equals(database, avoid, StringComparison.Ordinal)) database = "template1";
        return database;
    }

    /// <summary>The product name in messages, for example "PostgreSQL".</summary>
    protected virtual string ProductName => "PostgreSQL";

    /// <summary>The name of the time column that writes and downsampling use.</summary>
    protected virtual string TimeColumn => "time";

    /// <summary>A SQL condition on <c>n.nspname</c> that hides the schemas of the server itself.</summary>
    protected virtual string HiddenSchemas =>
        "n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast') AND n.nspname NOT LIKE 'pg\\_%'";

    // ------------------------------------------------------------------ helpers

    protected static CommandResult Ok(string message = "OK") => new(message, HttpStatusCode.OK, true);

    protected static CommandResult Failed(string message) =>
        new(string.IsNullOrWhiteSpace(message) ? "The command failed." : message, HttpStatusCode.BadRequest, false);

    /// <summary>Runs statements that change the server and turns a database error into a failed result with its message.</summary>
    protected async Task<CommandResult> ExecuteAsync(string? database, string sql, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        try
        {
            await Sql.ExecuteAsync(database, sql, parameters).ConfigureAwait(false);
            return Ok();
        }
        catch (PostgresException ex) { return Failed(ex.MessageText); }
    }

    protected static string Text(IList<object> row, int index) =>
        index < row.Count && row[index] != null ? Convert.ToString(row[index], CultureInfo.InvariantCulture) ?? "" : "";

    protected static IEnumerable<IList<object>> Rows(IReadOnlyList<ResultSeries> result) =>
        result.Count == 0 ? Enumerable.Empty<IList<object>>() : result[0].Values;

    protected static Dictionary<string, object?> Args(params (string Name, object? Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value);

    protected static NotSupportedException NotSupported(string what) => new($"{what} is not supported for this database.");

    // ------------------------------------------------------------------ databases

    public const string DatabaseNamesSql =
        "SELECT datname FROM pg_database WHERE datallowconn AND NOT datistemplate ORDER BY datname";

    public override async Task<IEnumerable<string>> GetDatabaseNamesAsync() =>
        Rows(await Sql.QueryAsync(CatalogDatabase(), DatabaseNamesSql).ConfigureAwait(false)).Select(r => Text(r, 0)).ToList();

    public override async Task<CommandResult> CreateDatabaseAsync(string database)
    {
        if (string.IsNullOrWhiteSpace(database)) throw new ArgumentNullException(nameof(database));
        return await ExecuteAsync(CatalogDatabase(), "CREATE DATABASE " + PostgresSql.Identifier(database)).ConfigureAwait(false);
    }

    public override async Task<CommandResult> DropDatabaseAsync(string database)
    {
        if (string.IsNullOrWhiteSpace(database)) throw new ArgumentNullException(nameof(database));
        // Other sessions on the database would block the drop, so end them first; this client's own pool goes too.
        (Sql as NpgsqlRunner)?.Release(database);
        var catalog = CatalogDatabase(database);
        var ended = await ExecuteAsync(catalog,
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @name AND pid <> pg_backend_pid()",
            Args(("name", database))).ConfigureAwait(false);
        if (!ended.Success) return ended;
        // DROP DATABASE cannot run in a transaction block, so it is a command of its own.
        return await ExecuteAsync(catalog, "DROP DATABASE " + PostgresSql.Identifier(database)).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ tables (measurements)

    /// <summary>The kinds of relation shown as measurements: tables, partitioned tables, views, materialized views, foreign tables.</summary>
    protected const string TableKinds = "c.relkind IN ('r', 'p', 'v', 'm', 'f')";

    protected string MeasurementNamesSql =>
        "SELECT n.nspname AS schema_name, c.relname AS table_name FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
        $"WHERE {TableKinds} AND {HiddenSchemas} " +
        "ORDER BY (n.nspname <> 'public'), n.nspname, c.relname";

    public override async Task<IEnumerable<string>> GetMeasurementNamesAsync(string database) =>
        Rows(await Sql.QueryAsync(database, MeasurementNamesSql).ConfigureAwait(false))
            .Select(r => PostgresSql.DisplayName(Text(r, 0), Text(r, 1))).ToList();

    public override async Task<CommandResult> DropMeasurementAsync(string database, string measurement)
    {
        var kind = Rows(await Sql.QueryAsync(database, "SELECT c.relkind::text FROM pg_class c WHERE c.oid = to_regclass(@name)",
            Args(("name", PostgresSql.Table(measurement)))).ConfigureAwait(false)).Select(r => Text(r, 0)).FirstOrDefault();
        var keyword = kind switch { "v" => "VIEW", "m" => "MATERIALIZED VIEW", "f" => "FOREIGN TABLE", _ => "TABLE" };
        return await ExecuteAsync(database, $"DROP {keyword} IF EXISTS {PostgresSql.Table(measurement)}").ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ columns (tags and fields)

    public const string ColumnsSql =
        "SELECT a.attname AS column_name, pg_catalog.format_type(a.atttypid, a.atttypmod) AS data_type " +
        "FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace " +
        "WHERE n.nspname = @schema AND c.relname = @table AND a.attnum > 0 AND NOT a.attisdropped ORDER BY a.attnum";

    /// <summary>The columns of a table with their types, in table order.</summary>
    protected async Task<IReadOnlyList<(string Name, string Type)>> GetColumnsAsync(string database, string measurement)
    {
        var (schema, table) = PostgresSql.SplitQualified(measurement);
        return Rows(await Sql.QueryAsync(database, ColumnsSql, Args(("schema", schema), ("table", table))).ConfigureAwait(false))
            .Select(r => (Text(r, 0), Text(r, 1))).ToList();
    }

    /// <summary>Text columns, which hold the values you group and filter by (the tags).</summary>
    public override async Task<IEnumerable<string>> GetTagKeysAsync(string database, string measurement) =>
        (await GetColumnsAsync(database, measurement).ConfigureAwait(false)).Where(c => PostgresSql.IsTextType(c.Type)).Select(c => c.Name).ToList();

    /// <summary>All the other columns (the fields): numbers, booleans and times, with their types.</summary>
    public override async Task<IEnumerable<FieldKey>> GetFieldKeysAsync(string database, string measurement) =>
        (await GetColumnsAsync(database, measurement).ConfigureAwait(false)).Where(c => !PostgresSql.IsTextType(c.Type))
            .Select(c => new FieldKey(c.Name, c.Type)).ToList();

    public override async Task<IEnumerable<TagValue>> GetTagValuesAsync(string database, string measurement, string tag)
    {
        var column = PostgresSql.Identifier(tag);
        var sql = $"SELECT DISTINCT {column}::text FROM {PostgresSql.Table(measurement)} WHERE {column} IS NOT NULL ORDER BY 1 LIMIT {MaxTagValues}";
        return Rows(await Sql.QueryAsync(database, sql).ConfigureAwait(false)).Select(r => new TagValue(tag, Text(r, 0))).ToList();
    }

    public string SchemaSql =>
        "SELECT n.nspname AS schema_name, c.relname AS table_name, a.attname AS column_name, " +
        "pg_catalog.format_type(a.atttypid, a.atttypmod) AS data_type " +
        "FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace " +
        $"WHERE {TableKinds} AND {HiddenSchemas} AND a.attnum > 0 AND NOT a.attisdropped " +
        "ORDER BY n.nspname, c.relname, a.attnum";

    /// <summary>Every table with its tag and field columns in one query (used by query IntelliSense).</summary>
    public override async Task<IReadOnlyList<MeasurementSchema>> GetDatabaseSchemaAsync(string database)
    {
        var tables = new Dictionary<string, (List<FieldKey> Fields, List<string> Tags)>(StringComparer.Ordinal);
        foreach (var row in Rows(await Sql.QueryAsync(database, SchemaSql).ConfigureAwait(false)))
        {
            var name = PostgresSql.DisplayName(Text(row, 0), Text(row, 1));
            if (!tables.TryGetValue(name, out var entry)) tables[name] = entry = (new List<FieldKey>(), new List<string>());
            var column = Text(row, 2);
            var type = Text(row, 3);
            if (PostgresSql.IsTextType(type)) entry.Tags.Add(column);
            else entry.Fields.Add(new FieldKey(column, type));
        }
        return tables.OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
            .Select(t => new MeasurementSchema(t.Key, t.Value.Fields, t.Value.Tags)).ToList();
    }

    // ------------------------------------------------------------------ series

    /// <summary>
    /// Series are the distinct tag combinations of a table, written like InfluxDB series keys
    /// (<c>cpu,host=web-1,region=eu</c>). A database with a better idea of a series overrides this.
    /// </summary>
    public override async Task<IEnumerable<string>> GetSeriesNamesAsync(string database, string measurement = null)
    {
        if (string.IsNullOrWhiteSpace(measurement)) return Array.Empty<string>();
        var tags = (await GetTagKeysAsync(database, measurement).ConfigureAwait(false)).Take(6).ToList();
        if (tags.Count == 0) return new[] { measurement };
        var keys = string.Join(" || ", tags.Select(t => $"{PostgresSql.Literal("," + t + "=")} || coalesce({PostgresSql.Identifier(t)}::text, '')"));
        var sql = $"SELECT DISTINCT {keys} FROM {PostgresSql.Table(measurement)} ORDER BY 1 LIMIT {MaxSeries}";
        return Rows(await Sql.QueryAsync(database, sql).ConfigureAwait(false)).Select(r => measurement + Text(r, 0)).ToList();
    }

    /// <summary>Deletes every row of the table but keeps the table.</summary>
    public override async Task<CommandResult> DropSeriesAsync(string database, string measurement = null)
    {
        if (string.IsNullOrWhiteSpace(measurement)) throw NotSupported("Deleting the data of a whole database");
        return await ExecuteAsync(database, "TRUNCATE TABLE " + PostgresSql.Table(measurement)).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ queries

    public override async Task<IEnumerable<ResultSeries>> QueryAsync(string database, string query) =>
        await Sql.QueryAsync(database, query).ConfigureAwait(false);

    public override async Task<IEnumerable<ResultSeries>> QueryAsync(string database, string query, CancellationToken cancellationToken) =>
        await Sql.QueryAsync(database, query, null, cancellationToken).ConfigureAwait(false);

    public const string RunningQueriesSql =
        "SELECT pid, datname, date_trunc('second', now() - query_start)::text AS duration, query FROM pg_stat_activity " +
        "WHERE state <> 'idle' AND pid <> pg_backend_pid() AND datname IS NOT NULL AND backend_type = 'client backend' ORDER BY query_start";

    public override async Task<IEnumerable<RunningQuery>> GetRunningQueriesAsync() =>
        Rows(await Sql.QueryAsync(null, RunningQueriesSql).ConfigureAwait(false))
            .Select(r => new RunningQuery(Convert.ToInt32(r[0], CultureInfo.InvariantCulture), Text(r, 1), Text(r, 2), Text(r, 3))).ToList();

    /// <summary>Cancels the query a session is running (the session stays connected).</summary>
    public override async Task<CommandResult> KillQueryAsync(int pid) =>
        await ExecuteAsync(null, "SELECT pg_cancel_backend(@pid)", Args(("pid", pid))).ConfigureAwait(false);

    // ------------------------------------------------------------------ writing

    /// <summary>The INSERT for one point: the time column, the tag columns, then the field columns, all as parameters.</summary>
    public (string Sql, Dictionary<string, object?> Parameters) BuildInsert(string measurement,
        IDictionary<string, object>? tags, IDictionary<string, object>? fields, DateTime timeStamp, int index = 0)
    {
        var columns = new List<string> { PostgresSql.Identifier(TimeColumn) };
        var parameters = new Dictionary<string, object?> { [$"p{index}_0"] = DateTime.SpecifyKind(timeStamp, DateTimeKind.Utc) };
        var names = new List<string> { $"@p{index}_0" };
        var i = 1;
        foreach (var pair in (tags ?? new Dictionary<string, object>()).Concat(fields ?? new Dictionary<string, object>()))
        {
            columns.Add(PostgresSql.Identifier(pair.Key));
            parameters[$"p{index}_{i}"] = pair.Value;
            names.Add($"@p{index}_{i}");
            i++;
        }
        return ($"INSERT INTO {PostgresSql.Table(measurement)} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", names)})", parameters);
    }

    public override Task<CommandResult> WriteAsync(string database, string measurement, IDictionary<string, object> tags,
        IDictionary<string, object> fields, DateTime timeStamp, string retentionPolicy = null)
    {
        var (sql, parameters) = BuildInsert(measurement, tags, fields, timeStamp);
        return ExecuteAsync(database, sql, parameters);
    }

    public override Task<CommandResult> WriteAsync(string database, DataPoint point, string retentionPolicy = null) =>
        WriteAsync(database, new[] { point }, retentionPolicy);

    public override Task<CommandResult> WriteAsync(string database, IEnumerable<DataPoint> points, string retentionPolicy = null)
    {
        var statements = new List<string>();
        var parameters = new Dictionary<string, object?>();
        var index = 0;
        foreach (var point in points)
        {
            var (sql, p) = BuildInsert(point.Measurement, point.Tags, point.Fields, point.TimeStamp, index++);
            statements.Add(sql);
            foreach (var pair in p) parameters[pair.Key] = pair.Value;
        }
        return statements.Count == 0 ? Task.FromResult(Ok("Nothing to write.")) : ExecuteAsync(database, string.Join(";\n", statements), parameters);
    }

    // ------------------------------------------------------------------ server

    public const string VersionSql = "SELECT version()";

    /// <summary>The version text shown by Ping. A database that reports more (an extension version) overrides this.</summary>
    protected virtual async Task<string> GetVersionTextAsync()
    {
        var text = Rows(await Sql.QueryAsync(null, VersionSql).ConfigureAwait(false)).Select(r => Text(r, 0)).FirstOrDefault() ?? "";
        // "PostgreSQL 16.3 (Debian 16.3-1) on x86_64..." -> "PostgreSQL 16.3"
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[0] + " " + parts[1] : (text.Length > 0 ? text : "unknown");
    }

    public override async Task<PingResponse> PingAsync()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var version = await GetVersionTextAsync().ConfigureAwait(false);
            return new PingResponse(true, watch.Elapsed, version);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException)
        {
            return new PingResponse(false, watch.Elapsed, "unavailable");
        }
    }

    public const string DiagnosticsSql =
        "SELECT current_setting('server_version') AS server_version, version() AS version, pg_postmaster_start_time() AS started, " +
        "now() AS current_time, current_database() AS database, current_user AS user_name, pg_backend_pid() AS pid, " +
        "coalesce(inet_server_addr()::text, '') AS address, coalesce(inet_server_port()::text, '') AS port, " +
        "current_setting('TimeZone') AS time_zone, current_setting('max_connections') AS max_connections, " +
        "(SELECT count(*) FROM pg_stat_activity)::text AS connections";

    /// <summary>Extra name/value rows a database adds to the diagnostics (for example the extension version).</summary>
    protected virtual Task<IEnumerable<KeyValuePair<string, string>>> GetExtraDiagnosticsAsync() =>
        Task.FromResult<IEnumerable<KeyValuePair<string, string>>>(Array.Empty<KeyValuePair<string, string>>());

    public override async Task<ServerDiagnostics> GetDiagnosticsAsync()
    {
        var row = Rows(await Sql.QueryAsync(null, DiagnosticsSql).ConfigureAwait(false)).FirstOrDefault();
        if (row == null) return null;
        var started = row[2] is DateTime s ? s : DateTime.MinValue;
        var now = row[3] is DateTime n ? n : DateTime.UtcNow;
        var uptime = started == DateTime.MinValue ? TimeSpan.Zero : now - started;
        var properties = new List<KeyValuePair<string, string>>
        {
            new("Server version", Text(row, 0)), new("Version", Text(row, 1)),
            new("Started", started.ToString("u", CultureInfo.InvariantCulture)),
            new("Current time", now.ToString("u", CultureInfo.InvariantCulture)),
            new("Uptime", $"{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m {uptime.Seconds}s"),
            new("Database", Text(row, 4)), new("User", Text(row, 5)), new("Backend PID", Text(row, 6)),
            new("Address", Text(row, 7)), new("Port", Text(row, 8)), new("Time zone", Text(row, 9)),
            new("Connections", Text(row, 11) + " of " + Text(row, 10)),
        };
        properties.AddRange(await GetExtraDiagnosticsAsync().ConfigureAwait(false));
        return new ServerDiagnostics
        {
            Hostname = Connection.Host, BuildVersion = Text(row, 0), Started = started, CurrentTime = now, Uptime = uptime,
            PID = Convert.ToInt64(row[6], CultureInfo.InvariantCulture), Properties = properties
        };
    }

    /// <summary>Statistic categories by name, each a query whose rows become one table.</summary>
    protected virtual IEnumerable<(string Name, string Sql)> StatisticQueries => new[]
    {
        ("Databases", "SELECT datname, numbackends, xact_commit, xact_rollback, blks_read, blks_hit, tup_returned, tup_fetched, " +
                      "tup_inserted, tup_updated, tup_deleted, conflicts, temp_files, temp_bytes, deadlocks, stats_reset " +
                      "FROM pg_stat_database WHERE datname IS NOT NULL ORDER BY datname"),
        ("Tables", "SELECT schemaname, relname, n_live_tup, n_dead_tup, seq_scan, idx_scan, n_tup_ins, n_tup_upd, n_tup_del, " +
                   "last_vacuum, last_autovacuum, last_analyze, last_autoanalyze FROM pg_stat_user_tables ORDER BY schemaname, relname"),
        ("Sessions", "SELECT datname, state, count(*) AS sessions FROM pg_stat_activity WHERE datname IS NOT NULL " +
                     "GROUP BY datname, state ORDER BY datname, state"),
    };

    public override async Task<ServerStats> GetStatsAsync()
    {
        var stats = new ServerStats();
        foreach (var (name, sql) in StatisticQueries)
        {
            try
            {
                var result = await Sql.QueryAsync(null, sql).ConfigureAwait(false);
                if (result.Count > 0 && result[0].Values.Count > 0)
                    stats.Custom[name] = new[] { new ResultSeries(name, result[0].Columns, result[0].Tags, result[0].Values) };
            }
            catch (PostgresException) { /* a statistics view this account may not read is left out */ }
        }
        return stats;
    }

    // ------------------------------------------------------------------ users (roles) and privileges

    public const string UsersSql =
        "SELECT rolname, rolsuper FROM pg_roles WHERE rolcanlogin AND rolname NOT LIKE 'pg\\_%' ORDER BY rolname";

    public override async Task<IEnumerable<DatabaseUser>> GetUsersAsync() =>
        Rows(await Sql.QueryAsync(CatalogDatabase(), UsersSql).ConfigureAwait(false))
            .Select(r => new DatabaseUser(Text(r, 0), r[1] is true)).ToList();

    public override async Task<CommandResult> CreateUserAsync(string username, string password, bool isAdmin) =>
        await ExecuteAsync(CatalogDatabase(),
            $"CREATE ROLE {PostgresSql.Identifier(username)} WITH LOGIN PASSWORD {PostgresSql.Literal(password)} {(isAdmin ? "SUPERUSER" : "NOSUPERUSER")}").ConfigureAwait(false);

    public override async Task<CommandResult> DropUserAsync(string username) =>
        await ExecuteAsync(CatalogDatabase(), "DROP ROLE " + PostgresSql.Identifier(username)).ConfigureAwait(false);

    public override async Task<CommandResult> SetPasswordAsync(string username, string password) =>
        await ExecuteAsync(CatalogDatabase(),
            $"ALTER ROLE {PostgresSql.Identifier(username)} WITH PASSWORD {PostgresSql.Literal(password)}").ConfigureAwait(false);

    public override async Task<CommandResult> GrantAdministratorAsync(string username) =>
        await ExecuteAsync(CatalogDatabase(), $"ALTER ROLE {PostgresSql.Identifier(username)} SUPERUSER").ConfigureAwait(false);

    public override async Task<CommandResult> RevokeAdministratorAsync(string username) =>
        await ExecuteAsync(CatalogDatabase(), $"ALTER ROLE {PostgresSql.Identifier(username)} NOSUPERUSER").ConfigureAwait(false);

    /// <summary>The user schemas of a database, where privileges are granted.</summary>
    protected string UserSchemasSql =>
        $"SELECT n.nspname FROM pg_namespace n WHERE {HiddenSchemas} ORDER BY n.nspname";

    public const string PrivilegeSummarySql =
        "SELECT count(*) AS tables, " +
        "count(*) FILTER (WHERE has_table_privilege(@user, c.oid, 'SELECT')) AS readable, " +
        "count(*) FILTER (WHERE has_table_privilege(@user, c.oid, 'INSERT')) AS writable " +
        "FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.relkind IN ('r', 'p', 'v', 'm', 'f') AND ";

    /// <summary>
    /// A user's rights per database, simplified to InfluxDB's three levels: Read when the user can SELECT from every table,
    /// Write when it can INSERT into every table, All when it can do both. Databases the user cannot connect to are left out.
    /// </summary>
    public override async Task<IEnumerable<DatabaseGrant>> GetPrivilegesAsync(string username)
    {
        var grants = new List<DatabaseGrant>();
        foreach (var database in await GetDatabaseNamesAsync().ConfigureAwait(false))
        {
            try
            {
                var row = Rows(await Sql.QueryAsync(database, PrivilegeSummarySql + HiddenSchemas, Args(("user", username))).ConfigureAwait(false)).FirstOrDefault();
                if (row == null) continue;
                var tables = Convert.ToInt64(row[0], CultureInfo.InvariantCulture);
                var readable = Convert.ToInt64(row[1], CultureInfo.InvariantCulture);
                var writable = Convert.ToInt64(row[2], CultureInfo.InvariantCulture);
                if (tables == 0) continue;
                var read = readable == tables;
                var write = writable == tables;
                if (read && write) grants.Add(new DatabaseGrant(database, DatabasePrivileges.All));
                else if (read) grants.Add(new DatabaseGrant(database, DatabasePrivileges.Read));
                else if (write) grants.Add(new DatabaseGrant(database, DatabasePrivileges.Write));
            }
            catch (PostgresException) { /* a database this account cannot connect to is skipped */ }
        }
        return grants;
    }

    private static string PrivilegeList(DatabasePrivileges privilege) => privilege switch
    {
        DatabasePrivileges.Read => "SELECT",
        DatabasePrivileges.Write => "INSERT, UPDATE, DELETE",
        _ => "ALL PRIVILEGES"
    };

    /// <summary>
    /// The statements that give a user a privilege on every table of the database's user schemas, now and for tables
    /// made later (default privileges). Granting replaces the earlier table privileges, so Edit Privilege can lower one.
    /// </summary>
    public static IEnumerable<string> GrantStatements(string database, string username, DatabasePrivileges privilege, IEnumerable<string> schemas)
    {
        var user = PostgresSql.Identifier(username);
        yield return $"GRANT CONNECT ON DATABASE {PostgresSql.Identifier(database)} TO {user}";
        foreach (var schema in schemas)
        {
            var s = PostgresSql.Identifier(schema);
            yield return $"GRANT USAGE ON SCHEMA {s} TO {user}";
            yield return $"REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA {s} FROM {user}";
            yield return $"ALTER DEFAULT PRIVILEGES IN SCHEMA {s} REVOKE ALL PRIVILEGES ON TABLES FROM {user}";
            if (privilege == DatabasePrivileges.None) continue;
            yield return $"GRANT {PrivilegeList(privilege)} ON ALL TABLES IN SCHEMA {s} TO {user}";
            yield return $"ALTER DEFAULT PRIVILEGES IN SCHEMA {s} GRANT {PrivilegeList(privilege)} ON TABLES TO {user}";
        }
    }

    public override async Task<CommandResult> GrantPrivilegeAsync(string username, DatabasePrivileges privilege, string database)
    {
        var schemas = Rows(await Sql.QueryAsync(database, UserSchemasSql).ConfigureAwait(false)).Select(r => Text(r, 0)).ToList();
        return await ExecuteAsync(database, string.Join(";\n", GrantStatements(database, username, privilege, schemas))).ConfigureAwait(false);
    }

    public override async Task<CommandResult> RevokePrivilegeAsync(string username, DatabasePrivileges privilege, string database)
    {
        var schemas = Rows(await Sql.QueryAsync(database, UserSchemasSql).ConfigureAwait(false)).Select(r => Text(r, 0)).ToList();
        return await ExecuteAsync(database, string.Join(";\n", GrantStatements(database, username, DatabasePrivileges.None, schemas))).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ what a database may not have

    public override Task<IEnumerable<RetentionPolicy>> GetRetentionPoliciesAsync(string database) =>
        throw NotSupported("Retention policies");

    public override Task<CommandResult> CreateRetentionPolicyAsync(string database, string policyName, string duration, int replication, bool isDefault = false) =>
        throw NotSupported("Retention policies");

    public override Task<CommandResult> AlterRetentionPolicyAsync(string database, string policyName, string duration, int replication, bool isDefault = false) =>
        throw NotSupported("Retention policies");

    public override Task<CommandResult> DropRetentionPolicyAsync(string database, string policyName) =>
        throw NotSupported("Retention policies");

    public override Task<IEnumerable<ContinuousQuery>> GetContinousQueriesAsync(string database) =>
        throw NotSupported("Continuous queries");

    public override Task<CommandResult> CreateContinuousQueryAsync(ContinuousQueryParams cqParams) =>
        throw NotSupported("Continuous queries");

    public override Task<CommandResult> DropContinuousQueryAsync(string database, string cqName) =>
        throw NotSupported("Continuous queries");

    public override Task<CommandResult> BackfillAsync(string database, BackfillParams backfillParams) =>
        throw NotSupported("Backfill");
}
