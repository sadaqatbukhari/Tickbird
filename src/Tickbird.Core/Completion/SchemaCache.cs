using Tickbird.Core.Data;

namespace Tickbird.Core.Completion;

/// <summary>
/// Database schemas (measurements, fields, tags) per connection and database, loaded once and shared by
/// every query tab. A failed load is retried on the next request; at most two loads run at a time.
/// </summary>
public sealed class SchemaCache
{
    private readonly object gate = new();
    private readonly Dictionary<(string Connection, string Database), Task<IReadOnlyList<MeasurementSchema>>> entries = new();
    private readonly Dictionary<(string Connection, string Database, string Measurement, string Tag), Task<IReadOnlyList<string>>> tagValues = new();
    private readonly SemaphoreSlim throttle = new(2);

    public Task<IReadOnlyList<MeasurementSchema>> GetAsync(string connectionId, string database,
        Func<Task<IReadOnlyList<MeasurementSchema>>> load)
    {
        var key = (connectionId, database);
        lock (gate)
        {
            if (entries.TryGetValue(key, out var existing) && !existing.IsFaulted && !existing.IsCanceled) return existing;
            var task = LoadAsync(load);
            entries[key] = task;
            return task;
        }
    }

    /// <summary>
    /// Values of one tag in one measurement, loaded the first time they are needed and then kept with the tag.
    /// A failed load is retried on the next request.
    /// </summary>
    public Task<IReadOnlyList<string>> GetTagValuesAsync(string connectionId, string database, string measurement, string tag,
        Func<Task<IReadOnlyList<string>>> load)
    {
        var key = (connectionId, database, measurement, tag);
        lock (gate)
        {
            if (tagValues.TryGetValue(key, out var existing) && !existing.IsFaulted && !existing.IsCanceled) return existing;
            var task = LoadAsync(load);
            tagValues[key] = task;
            return task;
        }
    }

    /// <summary>The schema if it has finished loading successfully; otherwise null.</summary>
    public IReadOnlyList<MeasurementSchema>? TryGetLoaded(string connectionId, string database)
    {
        lock (gate)
            return entries.TryGetValue((connectionId, database), out var task) && task.IsCompletedSuccessfully ? task.Result : null;
    }

    /// <summary>Forgets one database's schema, or every database of the connection when no database is given.</summary>
    public void Invalidate(string connectionId, string? database = null)
    {
        lock (gate)
        {
            foreach (var key in entries.Keys.Where(k => k.Connection == connectionId && (database == null || k.Database == database)).ToList())
                entries.Remove(key);
            foreach (var key in tagValues.Keys.Where(k => k.Connection == connectionId && (database == null || k.Database == database)).ToList())
                tagValues.Remove(key);
        }
    }

    private async Task<T> LoadAsync<T>(Func<Task<T>> load)
    {
        await throttle.WaitAsync();
        try { return await load(); }
        finally { throttle.Release(); }
    }
}
