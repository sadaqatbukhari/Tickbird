using Tickbird.Core.Data;

namespace Tickbird.Core.Providers;

/// <summary>
/// The databases Tickbird can connect to. To add one: add a value here, write a <see cref="DatabaseClient"/> for it,
/// and register a <see cref="DatabaseProfile"/> in <see cref="DatabaseProfiles"/>. The tree, menus, query editor,
/// results (grid, chart, map) and the connection dialog are driven by the profile and need no other change.
/// </summary>
public enum DatabaseKind
{
    InfluxDb1 = 0,
    InfluxDb3 = 3,
    TimescaleDb = 10,
    CrateDb = 11
}

/// <summary>What a database can do; the UI shows only the commands a database supports.</summary>
[Flags]
public enum DatabaseCapabilities
{
    None = 0,
    CreateDatabase = 1 << 0,
    DropDatabase = 1 << 1,
    /// <summary>Users or roles, with passwords and administrator rights.</summary>
    Users = 1 << 2,
    /// <summary>Read / write / all rights of a user on a database.</summary>
    Privileges = 1 << 3,
    /// <summary>Automatic removal of old data.</summary>
    RetentionPolicies = 1 << 4,
    /// <summary>Queries that keep a downsampled copy of the data up to date.</summary>
    ContinuousQueries = 1 << 5,
    /// <summary>Filling a downsampled copy from existing data.</summary>
    Backfill = 1 << 6,
    RunningQueries = 1 << 7,
    Statistics = 1 << 8,
    Diagnostics = 1 << 9,
    /// <summary>Lists the tag (text, grouping) columns of a measurement or table.</summary>
    TagKeys = 1 << 10,
    TagValues = 1 << 11,
    /// <summary>Lists the field (value) columns and their types.</summary>
    FieldKeys = 1 << 12,
    /// <summary>Lists the series, or the chunks, of a measurement or table.</summary>
    Series = 1 << 13,
    /// <summary>Deletes the data of a measurement or table but keeps it.</summary>
    DropSeries = 1 << 14,
    DropMeasurement = 1 << 15,
    WriteData = 1 << 16,
    /// <summary>Makes a user an administrator (superuser) or takes that away. A database without it shows users read-only in that respect.</summary>
    AdministratorRights = 1 << 17,
    /// <summary>Changes a single record (row or point) of a query result.</summary>
    EditRecords = 1 << 18,
    /// <summary>Inserts a single record.</summary>
    InsertRecords = 1 << 19,
    /// <summary>Deletes a single record.</summary>
    DeleteRecords = 1 << 20,
    /// <summary>All three record operations.</summary>
    AllRecordOperations = EditRecords | InsertRecords | DeleteRecords,
}

/// <summary>How a connection signs in.</summary>
public enum AuthenticationStyle
{
    /// <summary>User name and password; both can be left empty for an open server.</summary>
    UsernamePassword,
    /// <summary>A bearer or API token.</summary>
    Token
}

/// <summary>The words the UI uses for the database's concepts, so menus read naturally for each database.</summary>
public sealed record DatabaseTerms(
    string Measurement,
    string Measurements,
    string TagKeys,
    string TagValues,
    string FieldKeys,
    string Series,
    string DropSeries,
    string ContinuousQuery,
    string RetentionPolicy,
    string User,
    string ContinuousQueries,
    string RetentionPolicies,
    string Users)
{
    /// <summary>What the server's top level is called: "Database" for most, "Schema" where there is only one database.</summary>
    public string Database { get; init; } = "Database";

    public string Databases { get; init; } = "Databases";

    /// <summary>The InfluxDB words.</summary>
    public static readonly DatabaseTerms Influx = new("Measurement", "Measurements", "Tag Keys", "Tag Values", "Field Keys", "Series",
        "Drop Series", "Continuous Query", "Retention Policy", "User", "Continuous Queries", "Retention Policies", "Users");
}

/// <summary>Everything the app needs to know about one kind of database.</summary>
public sealed class DatabaseProfile
{
    public DatabaseProfile(DatabaseKind kind, string displayName, ushort defaultPort, bool isSql, AuthenticationStyle authentication,
        DatabaseCapabilities capabilities, DatabaseTerms terms, Func<DatabaseConnection, DatabaseClient> createClient)
    {
        Kind = kind;
        DisplayName = displayName;
        DefaultPort = defaultPort;
        IsSql = isSql;
        Authentication = authentication;
        Capabilities = capabilities;
        Terms = terms;
        CreateClient = createClient;
    }

    public DatabaseKind Kind { get; }

    /// <summary>Shown in the connection dialog and lists, for example "InfluxDB 1.x".</summary>
    public string DisplayName { get; }

    public ushort DefaultPort { get; }

    /// <summary>SQL, or InfluxQL when false. Decides the starter query, quoting and the completion list.</summary>
    public bool IsSql { get; }

    public AuthenticationStyle Authentication { get; }

    public DatabaseCapabilities Capabilities { get; }

    public DatabaseTerms Terms { get; }

    public Func<DatabaseConnection, DatabaseClient> CreateClient { get; }

    /// <summary>
    /// True when a retention policy belongs to one table (TimescaleDB) and not to the database (InfluxDB). The policy
    /// dialog then asks for a table and a duration only.
    /// </summary>
    public bool RetentionPerTable { get; init; }

    /// <summary>
    /// True when a continuous query writes into a destination measurement (InfluxDB). When false (TimescaleDB) the
    /// continuous query is a view with the given name, and the dialog leaves out the destination, fill type and resampling.
    /// </summary>
    public bool ContinuousQueryWritesToDestination { get; init; } = true;

    /// <summary>The database name offered when a connection names none (for a database that needs one to connect).</summary>
    public string? DefaultDatabase { get; init; }

    /// <summary>Databases that exist for the server itself and cannot be dropped from the tree.</summary>
    public IReadOnlyCollection<string> SystemDatabases { get; init; } = Array.Empty<string>();

    /// <summary>Extra keywords and functions for the editor's completion list (the database's own functions).</summary>
    public IReadOnlyList<(string Text, string Detail, bool IsFunction)> CompletionExtras { get; init; } = Array.Empty<(string, string, bool)>();

    /// <summary>
    /// False when the server has a single database that is not chosen by name (CrateDB). The connection dialog then
    /// leaves the Database field out.
    /// </summary>
    public bool UsesDatabaseName { get; init; } = true;

    /// <summary>The user name a new connection starts with (for example "crate" for CrateDB), or null for none.</summary>
    public string? DefaultUsername { get; init; }

    /// <summary>A short note shown in the connection dialog for this database (which port speaks which protocol...).</summary>
    public string? ConnectionHint { get; init; }

    /// <summary>
    /// Extra checks of a connection before it is saved or tested, beyond a name and a host. Returns the message to show,
    /// or null when the connection is fine.
    /// </summary>
    public Func<DatabaseConnection, string?>? ValidateConnection { get; init; }

    /// <summary>The first problem with a connection, or null. The name and host are always required.</summary>
    public string? Validate(DatabaseConnection connection)
    {
        if (string.IsNullOrWhiteSpace(connection.Name)) return "Connection name cannot be blank.";
        if (string.IsNullOrWhiteSpace(connection.Host)) return "Host cannot be blank.";
        return ValidateConnection?.Invoke(connection);
    }

    /// <summary>
    /// The URL schemes a pasted connection string may use for this database (<c>postgresql</c>, <c>crate</c>...). When
    /// several databases claim a scheme (<c>http</c>), the one whose default port matches wins.
    /// </summary>
    public IReadOnlyList<string> UrlSchemes { get; init; } = Array.Empty<string>();

    /// <summary>What to try when signing in fails, for example the default user of a new server. Shown with the diagnosis.</summary>
    public string? SignInHint { get; init; }

    /// <summary>The label of the server version in the Ping result, for example "InfluxDB Version".</summary>
    public string VersionLabel { get; init; } = "Version";

    /// <summary>Writes the starter query of a new query tab.</summary>
    public Func<string, string> SampleQuery { get; init; } = measurement => $"SELECT * FROM \"{measurement}\" LIMIT 100";

    public bool Supports(DatabaseCapabilities capability) => (Capabilities & capability) == capability;

    public bool IsSystemDatabase(string? name) =>
        name != null && SystemDatabases.Contains(name, StringComparer.OrdinalIgnoreCase);

    public override string ToString() => DisplayName;
}
