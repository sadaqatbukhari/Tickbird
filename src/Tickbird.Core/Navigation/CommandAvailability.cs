using Tickbird.Core.Data;
using Tickbird.Core.Providers;

namespace Tickbird.Core.Navigation;

public enum NodeKind { None, Connection, Database, Measurement, Loading, Group }

/// <summary>Which toolbar/menu commands are enabled (port of AppForm.UpdateUIState).</summary>
public sealed record CommandAvailability
{
    public bool Disconnect { get; init; }
    public bool Refresh { get; init; }
    public bool NewQuery { get; init; }
    public bool ShowQueries { get; init; }
    public bool ShowPolicies { get; init; }
    public bool ShowUsers { get; init; }
    public bool ShowStats { get; init; }
    public bool ShowDiagnostics { get; init; }
    public bool CreateDatabase { get; init; }
    public bool ContinuousQueries { get; init; }
    public bool Backfill { get; init; }
    public bool DropDatabase { get; init; }
    public bool TagKeys { get; init; }
    public bool TagValues { get; init; }
    public bool FieldKeys { get; init; }
    public bool ShowSeries { get; init; }
    public bool DropSeries { get; init; }
    public bool DropMeasurement { get; init; }
    public bool RunQuery { get; init; }
    public bool CommentQuery { get; init; }
    public bool OpenSaveQuery { get; init; }

    /// <summary>For an InfluxDB server generation (kept for callers that only know the old setting).</summary>
    public static CommandAvailability For(NodeKind kind, string? nodeName, InfluxDbServerVersion? serverVersion,
        bool requestTabSelected, bool queryTabSelected) =>
        For(kind, nodeName, DatabaseProfiles.For(serverVersion), requestTabSelected, queryTabSelected);

    /// <summary>
    /// Which commands are enabled for the selected node. What a connection can do comes from its database
    /// <paramref name="profile"/> (null means InfluxDB 1.x), so a new database needs no change here.
    /// </summary>
    public static CommandAvailability For(NodeKind kind, string? nodeName, DatabaseProfile? profile,
        bool requestTabSelected, bool queryTabSelected)
    {
        profile ??= DatabaseProfiles.Get(DatabaseKind.InfluxDb1);
        bool Can(DatabaseCapabilities capability) => profile.Supports(capability);
        var has = kind is NodeKind.Connection or NodeKind.Database or NodeKind.Measurement;
        var conn = kind == NodeKind.Connection;
        var db = kind == NodeKind.Database;
        var meas = kind == NodeKind.Measurement;
        return new CommandAvailability
        {
            Disconnect = has,
            Refresh = conn || db,
            NewQuery = db || meas,
            ShowQueries = conn && Can(DatabaseCapabilities.RunningQueries),
            ShowPolicies = conn && Can(DatabaseCapabilities.RetentionPolicies),
            ShowUsers = conn && Can(DatabaseCapabilities.Users),
            ShowStats = conn && Can(DatabaseCapabilities.Statistics),
            ShowDiagnostics = conn && Can(DatabaseCapabilities.Diagnostics),
            CreateDatabase = conn && Can(DatabaseCapabilities.CreateDatabase),
            ContinuousQueries = db && Can(DatabaseCapabilities.ContinuousQueries),
            Backfill = db && Can(DatabaseCapabilities.Backfill),
            DropDatabase = db && Can(DatabaseCapabilities.DropDatabase) && !profile.IsSystemDatabase(nodeName),
            TagKeys = meas && Can(DatabaseCapabilities.TagKeys),
            TagValues = meas && Can(DatabaseCapabilities.TagValues),
            FieldKeys = meas && Can(DatabaseCapabilities.FieldKeys),
            ShowSeries = meas && Can(DatabaseCapabilities.Series),
            DropSeries = meas && Can(DatabaseCapabilities.DropSeries),
            DropMeasurement = meas && Can(DatabaseCapabilities.DropMeasurement),
            RunQuery = requestTabSelected,
            CommentQuery = queryTabSelected,
            OpenSaveQuery = queryTabSelected,
        };
    }
}
