using Tickbird.Core.Data;
using Tickbird.Core.Completion;
using Tickbird.Core.History;
using Tickbird.Core.Query;
using Tickbird.Core.Query;
using Tickbird.Core.Settings;

namespace Tickbird.Desktop.Services;

/// <summary>Application-wide state: persisted settings and the set of open server connections.</summary>
public sealed class TickbirdApp
{
    public static TickbirdApp Current { get; set; } = new(new SettingsStore(PlatformPaths.GetCurrentSettingsFile()));

    private readonly SettingsStore store;

    public TickbirdApp(SettingsStore store)
    {
        this.store = store;
        Settings = store.Load();
        DatabaseClientFactory.AllowUntrustedSsl = () => Settings.AllowUntrustedSsl;
    }

    public TickbirdSettings Settings { get; private set; }

    /// <summary>Queries run from the editor (query-history.json next to the settings file).</summary>
    public QueryHistory History => history ??= new QueryHistory(QueryHistory.PathNextTo(store.FilePath), retentionDays: Settings.QueryHistoryRetentionDays);
    private QueryHistory? history;

    /// <summary>Saved query snippets (query-snippets.json next to the settings file).</summary>
    public QuerySnippets Snippets => snippets ??= new QuerySnippets(QuerySnippets.PathNextTo(store.FilePath));
    private QuerySnippets? snippets;
    public List<DatabaseClient> ActiveClients { get; } = new();
    public string SettingsFilePath => store.FilePath;

    public ResultFormatter Formatter => new(Settings.DateFormat, Settings.TimeFormat);

    public void Save() => store.Save(Settings);

    public void ReplaceSettings(TickbirdSettings settings)
    {
        Settings = settings;
        Save();
    }

    public DatabaseClient? GetClient(DatabaseConnection connection) =>
        ActiveClients.FirstOrDefault(c => c.Connection.Id == connection.Id);

    public DatabaseClient Connect(DatabaseConnection connection)
    {
        var existing = GetClient(connection);
        if (existing != null) return existing;
        var client = DatabaseClientFactory.Create(connection);
        ActiveClients.Add(client);
        return client;
    }

    public void Disconnect(DatabaseConnection connection)
    {
        ActiveClients.RemoveAll(c => c.Connection.Id == connection.Id);
        Schemas.Invalidate(connection.Id);
    }

    public bool IsConnected(DatabaseConnection connection) => GetClient(connection) != null;

    public CompletionProvider CreateCompletionProvider() => new();

    /// <summary>Measurements, fields and tags per connection and database, shared by all query tabs.</summary>
    public SchemaCache Schemas { get; } = new();

    /// <summary>The database schema for IntelliSense: loaded once (in the background when the server connects) and cached.</summary>
    public Task<IReadOnlyList<MeasurementSchema>> GetSchemaAsync(DatabaseConnection connection, string database)
    {
        var client = Connect(connection);
        return Schemas.GetAsync(connection.Id, database, () => client.GetDatabaseSchemaAsync(database));
    }

    /// <summary>Values of a tag in a measurement, loaded once when first needed and kept with the tag.</summary>
    public Task<IReadOnlyList<string>> GetTagValuesAsync(DatabaseConnection connection, string database, string measurement, string tag)
    {
        var client = Connect(connection);
        return Schemas.GetTagValuesAsync(connection.Id, database, measurement, tag, async () =>
            (IReadOnlyList<string>)(await client.GetTagValuesAsync(database, measurement, tag) ?? Enumerable.Empty<TagValue>())
                .Select(v => v.Value).Where(v => v != null).ToList());
    }

    /// <summary>Starts loading schemas in the background; failures are ignored here and retried when a query tab asks.</summary>
    public void PrefetchSchemas(DatabaseConnection connection, IEnumerable<string> databases)
    {
        foreach (var database in databases)
            if (!string.IsNullOrWhiteSpace(database)) _ = GetSchemaAsync(connection, database).ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }
}
