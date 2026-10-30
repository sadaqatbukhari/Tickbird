using Tickbird.Core.Data;

namespace Tickbird.Core.Providers;

/// <summary>The databases Tickbird knows. Register another <see cref="DatabaseProfile"/> here to add a database.</summary>
public static class DatabaseProfiles
{
    private const DatabaseCapabilities InfluxFull =
        DatabaseCapabilities.CreateDatabase | DatabaseCapabilities.DropDatabase | DatabaseCapabilities.Users
        | DatabaseCapabilities.Privileges | DatabaseCapabilities.RetentionPolicies | DatabaseCapabilities.ContinuousQueries
        | DatabaseCapabilities.Backfill | DatabaseCapabilities.RunningQueries | DatabaseCapabilities.Statistics
        | DatabaseCapabilities.Diagnostics | DatabaseCapabilities.TagKeys | DatabaseCapabilities.TagValues
        | DatabaseCapabilities.FieldKeys | DatabaseCapabilities.Series | DatabaseCapabilities.DropSeries
        | DatabaseCapabilities.DropMeasurement | DatabaseCapabilities.WriteData;

    private static readonly Dictionary<DatabaseKind, DatabaseProfile> Registered = new();

    static DatabaseProfiles()
    {
        Register(new DatabaseProfile(DatabaseKind.InfluxDb1, "InfluxDB 1.x", 8086, isSql: false, AuthenticationStyle.UsernamePassword,
            InfluxFull, DatabaseTerms.Influx, connection => new OfficialInfluxDb1Client(connection))
        {
            SystemDatabases = new[] { "_internal" },
            VersionLabel = "InfluxDB Version",
            SampleQuery = measurement => $"SELECT * FROM \"{measurement}\" WHERE time > now() - 5m LIMIT 100"
        });

        Register(new DatabaseProfile(DatabaseKind.InfluxDb3, "InfluxDB 3.x", 8181, isSql: true, AuthenticationStyle.Token,
            DatabaseCapabilities.CreateDatabase | DatabaseCapabilities.DropDatabase | DatabaseCapabilities.TagKeys
            | DatabaseCapabilities.TagValues | DatabaseCapabilities.FieldKeys | DatabaseCapabilities.DropMeasurement
            | DatabaseCapabilities.WriteData, DatabaseTerms.Influx, connection => new InfluxDb3Client(connection))
        {
            SystemDatabases = new[] { "_internal" },
            VersionLabel = "InfluxDB Version",
            SampleQuery = measurement => $"SELECT * FROM \"{measurement}\" WHERE time > now() - INTERVAL '5 minutes' LIMIT 100"
        });

        Register(TimescaleDbClient.Profile);
    }

    /// <summary>Adds a database (or replaces the profile of a kind).</summary>
    public static void Register(DatabaseProfile profile) => Registered[profile.Kind] = profile;

    /// <summary>Removes a database again (used by tests and by anything that adds databases at run time).</summary>
    public static bool Unregister(DatabaseKind kind) => Registered.Remove(kind);

    /// <summary>Every database, in the order the connection dialog lists them.</summary>
    public static IReadOnlyList<DatabaseProfile> All => Registered.Values.OrderBy(p => (int)p.Kind).ToList();

    public static DatabaseProfile Get(DatabaseKind kind) =>
        Registered.TryGetValue(kind, out var profile) ? profile : throw new NotSupportedException($"{kind} is not a supported database.");

    /// <summary>The profile of a connection (InfluxDB 1.x for connections saved before other databases existed).</summary>
    public static DatabaseProfile For(DatabaseConnection connection) => Get(connection.Kind);

    /// <summary>The profile for an InfluxDB server generation (used by code that only knows the old setting).</summary>
    public static DatabaseProfile For(InfluxDbServerVersion? version) =>
        Get(version == InfluxDbServerVersion.InfluxDb3 ? DatabaseKind.InfluxDb3 : DatabaseKind.InfluxDb1);
}
