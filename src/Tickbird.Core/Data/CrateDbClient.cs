using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Npgsql;
using Tickbird.Core.Data.Sql;
using Tickbird.Core.Providers;

namespace Tickbird.Core.Data;

/// <summary>
/// CrateDB, over its PostgreSQL protocol (port 5432). It maps onto the same concepts as InfluxDB so every view works:
/// a table is a measurement, its text columns are tags and its other columns fields, a partition is a "series", and
/// a user is a user. CrateDB has one database (<c>crate</c>) whose tables live in schemas; tables in the default
/// schema <c>doc</c> are shown by their plain name and the others as <c>schema.table</c>.
/// It derives from <see cref="PostgresClientBase"/> and only overrides what CrateDB does differently: it has no
/// <c>pg_class</c>-style catalog worth relying on (so <c>information_schema</c> and <c>sys</c> are used), no
/// <c>TRUNCATE</c>, no several statements in one command, and its own users, privileges and jobs.
/// </summary>
public sealed class CrateDbClient : PostgresClientBase
{
    /// <summary>The one database of a CrateDB server.</summary>
    public const string CatalogName = "crate";

    /// <summary>Where a table without a schema lives.</summary>
    public const string DefaultSchemaName = "doc";

    private const DatabaseCapabilities Capabilities =
        DatabaseCapabilities.Users | DatabaseCapabilities.Privileges | DatabaseCapabilities.RunningQueries
        | DatabaseCapabilities.Statistics | DatabaseCapabilities.Diagnostics | DatabaseCapabilities.TagKeys
        | DatabaseCapabilities.TagValues | DatabaseCapabilities.FieldKeys | DatabaseCapabilities.Series
        | DatabaseCapabilities.DropSeries | DatabaseCapabilities.DropMeasurement | DatabaseCapabilities.WriteData
        | DatabaseCapabilities.AllRecordOperations;

    /// <summary>How CrateDB looks in the app.</summary>
    public static readonly DatabaseProfile Profile = new(DatabaseKind.CrateDb, "CrateDB", 5432, isSql: true,
        AuthenticationStyle.UsernamePassword, Capabilities,
        new DatabaseTerms("Table", "Tables", "Text Columns", "Column Values", "Columns", "Partitions", "Delete All Rows",
            "Continuous Aggregate", "Retention Policy", "User", "Continuous Aggregates", "Retention Policies", "Users"),
        connection => new CrateDbClient(connection))
    {
        UsesDatabaseName = false,
        DefaultDatabase = CatalogName,
        DefaultUsername = "crate",
        VersionLabel = "CrateDB Version",
        UrlSchemes = new[] { "crate", "cratedb" },
        SignInHint = "A new CrateDB accepts the user crate with no password.",
        ConnectionHint = "Connects over the PostgreSQL protocol on port 5432 (the Admin UI and HTTP use 4200). A new CrateDB has the user crate with no password.",
        ValidateConnection = connection => connection.Port == HttpPort
            ? "Port 4200 is CrateDB's HTTP port. Tickbird connects over the PostgreSQL protocol, by default on port 5432."
            : null,
        SampleQuery = table => $"SELECT * FROM {PostgresSql.Table(table, DefaultSchemaName)} LIMIT 100",
        CompletionExtras = new (string, string, bool)[]
        {
            ("date_bin()", "CrateDB: group timestamps into fixed-size bins", true),
            ("date_trunc()", "Cut a timestamp down to a unit", true),
            ("current_timestamp", "The current time", false),
            ("now()", "The current time", true),
            ("percentile()", "Percentile of a column", true),
            ("max_by()", "Value of one column where another is largest", true),
            ("min_by()", "Value of one column where another is smallest", true),
            ("array_agg()", "Collect values into an array", true),
            ("distance()", "Distance in meters between two geo points", true),
            ("within()", "Is a geo point inside a shape", true),
            ("geohash()", "Geohash of a geo point", true),
            ("regexp_matches()", "Match text with a regular expression", true),
            ("generate_series()", "A series of numbers or times", true),
            ("sys.nodes", "CrateDB: the nodes of the cluster", false),
            ("sys.shards", "CrateDB: the shards of all tables", false),
            ("sys.jobs", "CrateDB: queries running now", false),
            ("information_schema.tables", "CrateDB: all tables and views", false),
            ("REFRESH TABLE", "CrateDB: make written rows visible to queries", false),
            ("OPTIMIZE TABLE", "CrateDB: merge segments of a table", false),
            ("SHOW CREATE TABLE", "CrateDB: the statement that creates a table", false),
            ("COPY", "CrateDB: import or export files", false),
        }
    };

    /// <summary>CrateDB's HTTP port (the Admin UI), which does not speak the PostgreSQL protocol.</summary>
    public const ushort HttpPort = 4200;

    public CrateDbClient(DatabaseConnection connection, ISqlRunner? runner = null)
        : base(connection, runner ?? new NpgsqlRunner(connection, CatalogName, ServerCompatibilityMode.NoTypeLoading))
    {
    }

    protected override string ProductName => "CrateDB";

    protected override string MaintenanceDatabase => CatalogName;

    protected override string DefaultSchema => DefaultSchemaName;

    /// <summary>CrateDB has one database, so the connection's Database field means nothing.</summary>
    protected override string CatalogDatabase(string? avoid = null) => CatalogName;

    // ------------------------------------------------------------------ catalog (information_schema)

    /// <summary>A SQL condition on <c>table_schema</c> that hides the schemas of the server itself.</summary>
    public const string UserSchemas = "table_schema NOT IN ('sys', 'information_schema', 'pg_catalog', 'blob')";

    /// <summary>Real columns only: the sub-columns of an object are listed too, as <c>obj['key']</c>.</summary>
    public const string RealColumns = "column_name NOT LIKE '%[%'";

    public const string ConnectivitySql = "SELECT name FROM sys.cluster";

    /// <summary>Asks the server something, so connecting is checked, and reports its one database.</summary>
    public override async Task<IEnumerable<string>> GetDatabaseNamesAsync()
    {
        await Sql.QueryAsync(CatalogName, ConnectivitySql).ConfigureAwait(false);
        return new[] { CatalogName };
    }

    public override Task<CommandResult> CreateDatabaseAsync(string database) => throw NotSupported("Creating a database");

    public override Task<CommandResult> DropDatabaseAsync(string database) => throw NotSupported("Dropping a database");

    protected override string MeasurementNamesSql =>
        $"SELECT table_schema, table_name FROM information_schema.tables WHERE {UserSchemas} " +
        "ORDER BY (table_schema <> 'doc'), table_schema, table_name";

    public const string CrateColumnsSql =
        "SELECT column_name, data_type FROM information_schema.columns " +
        "WHERE table_schema = @schema AND table_name = @table AND " + RealColumns + " ORDER BY ordinal_position";

    protected override string ColumnsQuery => CrateColumnsSql;

    public const string CratePrimaryKeySql =
        "SELECT k.column_name FROM information_schema.key_column_usage k JOIN information_schema.table_constraints t " +
        "ON t.constraint_name = k.constraint_name AND t.table_schema = k.table_schema AND t.table_name = k.table_name " +
        "WHERE t.constraint_type = 'PRIMARY KEY' AND k.table_schema = @schema AND k.table_name = @table ORDER BY k.ordinal_position";

    protected override string PrimaryKeyQuery => CratePrimaryKeySql;

    /// <summary>CrateDB shows new and changed rows to queries only after a refresh.</summary>
    protected override async Task AfterRecordWrittenAsync(string database, string table)
    {
        try { await Sql.ExecuteAsync(database, "REFRESH TABLE " + TableRef(table)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException) { /* the write itself succeeded */ }
    }

    public override string SchemaSql =>
        "SELECT table_schema, table_name, column_name, data_type FROM information_schema.columns " +
        $"WHERE {UserSchemas} AND {RealColumns} ORDER BY table_schema, table_name, ordinal_position";

    /// <summary>Text columns and IP addresses are the values you group and filter by.</summary>
    protected override bool IsTagType(string? dataType) =>
        base.IsTagType(dataType) || string.Equals(dataType, "ip", StringComparison.OrdinalIgnoreCase);

    public const string RelationTypeSql =
        "SELECT table_type FROM information_schema.tables WHERE table_schema = @schema AND table_name = @table";

    protected override async Task<string> GetRelationKeywordAsync(string database, string measurement)
    {
        var (schema, table) = SplitName(measurement);
        var type = Rows(await Sql.QueryAsync(database, RelationTypeSql, Args(("schema", schema), ("table", table))).ConfigureAwait(false))
            .Select(r => Text(r, 0)).FirstOrDefault();
        return string.Equals(type, "VIEW", StringComparison.OrdinalIgnoreCase) ? "VIEW" : "TABLE";
    }

    /// <summary>CrateDB has no TRUNCATE; deleting every row does the same.</summary>
    protected override string ClearTableStatement(string measurement) => "DELETE FROM " + TableRef(measurement);

    // ------------------------------------------------------------------ partitions as series

    public const string PartitionsSql =
        "SELECT partition_ident, \"values\" FROM information_schema.table_partitions " +
        "WHERE table_schema = @schema AND table_name = @table ORDER BY partition_ident LIMIT 1000";

    /// <summary>The partitions of a table as series keys: <c>events,partition=04732cpp6ksjcc9i60o30c1g,values={day=...}</c>.</summary>
    public override async Task<IEnumerable<string>> GetSeriesNamesAsync(string database, string measurement = null)
    {
        if (string.IsNullOrWhiteSpace(measurement)) return Array.Empty<string>();
        var (schema, table) = SplitName(measurement);
        var partitions = Rows(await Sql.QueryAsync(database, PartitionsSql, Args(("schema", schema), ("table", table))).ConfigureAwait(false))
            .Select(r => $"{measurement},partition={Text(r, 0)},values={Text(r, 1)}").ToList();
        // A table that is not partitioned: show its distinct tag combinations instead.
        return partitions.Count > 0 ? partitions : await base.GetSeriesNamesAsync(database, measurement).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ running several statements

    /// <summary>CrateDB takes one statement per command, so a batch runs statement by statement and stops at the first failure.</summary>
    protected override async Task<CommandResult> ExecuteBatchAsync(string? database, IReadOnlyList<string> statements,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        var result = Ok();
        foreach (var statement in statements)
        {
            result = await ExecuteAsync(database, statement, parameters).ConfigureAwait(false);
            if (!result.Success) return result;
        }
        return result;
    }

    // ------------------------------------------------------------------ server

    public const string VersionNumberSql = "SELECT version['number'] FROM sys.nodes ORDER BY name LIMIT 1";

    protected override async Task<string> GetVersionTextAsync()
    {
        var number = Rows(await Sql.QueryAsync(null, VersionNumberSql).ConfigureAwait(false)).Select(r => Text(r, 0)).FirstOrDefault();
        return string.IsNullOrEmpty(number) ? "CrateDB" : "CrateDB " + number;
    }

    public const string NodeSql =
        "SELECT name, hostname, version['number'] AS version, version['build_hash'] AS build_hash, os['uptime'] AS host_uptime, current_timestamp AS current_time, " +
        "os_info['available_processors'] AS processors, heap['max'] AS heap_max FROM sys.nodes ORDER BY name LIMIT 1";

    public const string ClusterSql = "SELECT name, master_node FROM sys.cluster";

    public const string NodeCountSql = "SELECT count(*) FROM sys.nodes";

    public const string CurrentUserSql = "SELECT current_user";

    private static DateTime ToUtc(object? value) => value switch
    {
        DateTime dateTime => dateTime.Kind == DateTimeKind.Utc ? dateTime : DateTime.SpecifyKind(dateTime, DateTimeKind.Utc),
        DateTimeOffset offset => offset.UtcDateTime,
        long milliseconds => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime,
        _ => DateTime.MinValue
    };

    public override async Task<ServerDiagnostics> GetDiagnosticsAsync()
    {
        var node = Rows(await Sql.QueryAsync(null, NodeSql).ConfigureAwait(false)).FirstOrDefault();
        if (node == null) return null;
        // CrateDB does not report when a node started, only how long the host it runs on has been up.
        var now = node[5] is { } n ? ToUtc(n) : DateTime.UtcNow;
        var hostUptime = node[4] is null ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Convert.ToDouble(node[4], CultureInfo.InvariantCulture));
        var properties = new List<KeyValuePair<string, string>>
        {
            new("Node", Text(node, 0)), new("Host name", Text(node, 1)), new("Version", Text(node, 2)), new("Build", Text(node, 3)),
            new("Current time", now.ToString("u", CultureInfo.InvariantCulture)),
            new("Host uptime", $"{hostUptime.Days}d {hostUptime.Hours}h {hostUptime.Minutes}m {hostUptime.Seconds}s"),
            new("Processors", Text(node, 6)), new("Maximum heap", Text(node, 7)),
        };
        await AddAsync(properties, "Cluster", ClusterSql, r => Text(r, 0)).ConfigureAwait(false);
        await AddAsync(properties, "Master node", ClusterSql, r => Text(r, 1)).ConfigureAwait(false);
        await AddAsync(properties, "Nodes", NodeCountSql, r => Text(r, 0)).ConfigureAwait(false);
        await AddAsync(properties, "User", CurrentUserSql, r => Text(r, 0)).ConfigureAwait(false);
        return new ServerDiagnostics
        {
            Hostname = Connection.Host, BuildVersion = Text(node, 2), Started = DateTime.MinValue, CurrentTime = now, Uptime = hostUptime, PID = 0,
            Properties = properties
        };
    }

    /// <summary>Adds one fact from a query; a fact this account may not read is left out.</summary>
    private async Task AddAsync(List<KeyValuePair<string, string>> properties, string name, string sql, Func<IList<object>, string> value)
    {
        try
        {
            var row = Rows(await Sql.QueryAsync(null, sql).ConfigureAwait(false)).FirstOrDefault();
            if (row != null) properties.Add(new(name, value(row)));
        }
        catch (PostgresException) { /* not allowed for this user */ }
    }

    protected override IEnumerable<(string Name, string Sql)> StatisticQueries => new[]
    {
        ("Nodes", "SELECT name, hostname, version['number'] AS version, load['1'] AS load_1, load['5'] AS load_5, " +
                  "heap['used'] AS heap_used, heap['max'] AS heap_max, fs['total']['used'] AS disk_used, fs['total']['size'] AS disk_size " +
                  "FROM sys.nodes ORDER BY name"),
        ("Tables", "SELECT table_schema, table_name, number_of_shards, number_of_replicas, partitioned_by, clustered_by " +
                   $"FROM information_schema.tables WHERE {UserSchemas} ORDER BY table_schema, table_name"),
        ("Shards", "SELECT schema_name, table_name, count(*) AS shards, sum(num_docs) AS rows, sum(size) AS bytes " +
                   "FROM sys.shards WHERE \"primary\" GROUP BY schema_name, table_name ORDER BY schema_name, table_name"),
        ("Health", "SELECT table_schema, table_name, health, severity, missing_shards, underreplicated_shards " +
                   "FROM sys.health ORDER BY severity DESC, table_schema, table_name"),
    };

    // ------------------------------------------------------------------ running queries

    public const string JobsSql =
        "SELECT id, started, current_timestamp AS current_time, stmt FROM sys.jobs WHERE stmt NOT LIKE '%sys.jobs%' ORDER BY started";

    private readonly ConcurrentDictionary<int, string> jobIds = new();

    /// <summary>
    /// The number the list shows for a job: the first 32 bits of its id. A job id is a UUID and the list wants an
    /// integer, so this is how a row is mapped back to the job to cancel.
    /// </summary>
    public static int JobNumber(string jobId)
    {
        var hex = new string(jobId.Where(Uri.IsHexDigit).Take(8).ToArray());
        return hex.Length == 0 ? 0 : unchecked((int)uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    /// <summary>A duration the way the running queries list sorts it: <c>1h2m3s</c>, <c>4m5s</c>, <c>6s</c>, <c>250ms</c>.</summary>
    public static string FormatDuration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span < TimeSpan.FromSeconds(1)) return $"{(int)span.TotalMilliseconds}ms";
        var text = new StringBuilder();
        if (span.TotalHours >= 1) text.Append((int)span.TotalHours).Append('h');
        if (span.TotalMinutes >= 1) text.Append(span.Minutes).Append('m');
        return text.Append(span.Seconds).Append('s').ToString();
    }

    public override async Task<IEnumerable<RunningQuery>> GetRunningQueriesAsync()
    {
        var rows = Rows(await Sql.QueryAsync(null, JobsSql).ConfigureAwait(false)).ToList();
        jobIds.Clear();
        var queries = new List<RunningQuery>();
        foreach (var row in rows)
        {
            var id = Text(row, 0);
            var number = JobNumber(id);
            jobIds[number] = id;
            queries.Add(new RunningQuery(number, CatalogName, FormatDuration(ToUtc(row[2]) - ToUtc(row[1])), Text(row, 3)));
        }
        return queries;
    }

    /// <summary>Stops a job. The id comes from the last listing and is checked to be a UUID before it is put in the statement.</summary>
    public override async Task<CommandResult> KillQueryAsync(int pid)
    {
        if (!jobIds.TryGetValue(pid, out var id) || !Guid.TryParse(id, out var guid))
            return Failed("That query is no longer running. Refresh the list and try again.");
        return await ExecuteAsync(null, "KILL " + PostgresSql.StringLiteral(guid.ToString())).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ users and privileges

    public const string CrateUsersSql = "SELECT name, superuser FROM sys.users ORDER BY name";

    public override async Task<IEnumerable<DatabaseUser>> GetUsersAsync() =>
        Rows(await Sql.QueryAsync(CatalogName, CrateUsersSql).ConfigureAwait(false))
            .Select(r => new DatabaseUser(Text(r, 0), r[1] is true)).ToList();

    public override async Task<CommandResult> CreateUserAsync(string username, string password, bool isAdmin)
    {
        if (isAdmin) return Failed("CrateDB cannot make a user a superuser with SQL; the superuser is set in crate.yml.");
        return await ExecuteAsync(CatalogName,
            $"CREATE USER {PostgresSql.Identifier(username)}{PasswordClause(password, "WITH")}").ConfigureAwait(false);
    }

    /// <summary>CrateDB writes the password as a property: <c>WITH (password = '...')</c>. An empty password leaves it out.</summary>
    public static string PasswordClause(string? password, string keyword) =>
        string.IsNullOrEmpty(password) ? "" : $" {keyword} (password = {PostgresSql.StringLiteral(password)})";

    public override async Task<CommandResult> DropUserAsync(string username) =>
        await ExecuteAsync(CatalogName, "DROP USER " + PostgresSql.Identifier(username)).ConfigureAwait(false);

    public override async Task<CommandResult> SetPasswordAsync(string username, string password) =>
        await ExecuteAsync(CatalogName,
            $"ALTER USER {PostgresSql.Identifier(username)} SET (password = {(string.IsNullOrEmpty(password) ? "NULL" : PostgresSql.StringLiteral(password))})").ConfigureAwait(false);

    public override Task<CommandResult> GrantAdministratorAsync(string username) => throw NotSupported("Administrator rights");

    public override Task<CommandResult> RevokeAdministratorAsync(string username) => throw NotSupported("Administrator rights");

    public const string PrivilegesSql =
        "SELECT type FROM sys.privileges WHERE grantee = @user AND class = 'CLUSTER' AND state = 'GRANT'";

    /// <summary>
    /// A user's rights simplified to InfluxDB's three levels, for the whole cluster: DQL (queries) is Read, DML (inserts,
    /// updates, deletes) is Write, and both together are All. The database name is the one database, <c>crate</c>.
    /// </summary>
    public override async Task<IEnumerable<DatabaseGrant>> GetPrivilegesAsync(string username)
    {
        var types = Rows(await Sql.QueryAsync(CatalogName, PrivilegesSql, Args(("user", username))).ConfigureAwait(false))
            .Select(r => Text(r, 0).ToUpperInvariant()).ToHashSet();
        var all = types.Contains("AL");
        var read = all || types.Contains("DQL");
        var write = all || types.Contains("DML");
        if (read && write) return new[] { new DatabaseGrant(CatalogName, DatabasePrivileges.All) };
        if (read) return new[] { new DatabaseGrant(CatalogName, DatabasePrivileges.Read) };
        if (write) return new[] { new DatabaseGrant(CatalogName, DatabasePrivileges.Write) };
        return Array.Empty<DatabaseGrant>();
    }

    /// <summary>
    /// The statements that give a user a privilege on the whole cluster. The earlier rights are taken away first, so
    /// Edit Privilege can lower one. <see cref="DatabasePrivileges.None"/> only takes them away.
    /// </summary>
    public static IEnumerable<string> PrivilegeStatements(string username, DatabasePrivileges privilege)
    {
        var user = PostgresSql.Identifier(username);
        yield return $"REVOKE DQL, DML, DDL FROM {user}";
        var granted = privilege switch
        {
            DatabasePrivileges.Read => "DQL",
            DatabasePrivileges.Write => "DML",
            DatabasePrivileges.All => "DQL, DML, DDL",
            _ => null
        };
        if (granted != null) yield return $"GRANT {granted} TO {user}";
    }

    public override async Task<CommandResult> GrantPrivilegeAsync(string username, DatabasePrivileges privilege, string database) =>
        await ExecuteBatchAsync(CatalogName, PrivilegeStatements(username, privilege).ToList()).ConfigureAwait(false);

    public override async Task<CommandResult> RevokePrivilegeAsync(string username, DatabasePrivileges privilege, string database) =>
        await ExecuteBatchAsync(CatalogName, PrivilegeStatements(username, DatabasePrivileges.None).ToList()).ConfigureAwait(false);
}
