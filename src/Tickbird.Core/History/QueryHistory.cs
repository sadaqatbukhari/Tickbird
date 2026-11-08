using Newtonsoft.Json;

namespace Tickbird.Core.History;

/// <summary>How a query run ended.</summary>
public enum QueryRunStatus
{
    Succeeded,
    Failed,
    Cancelled,
    TimedOut
}

/// <summary>One query run from the editor.</summary>
public sealed class QueryHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime RunAtUtc { get; set; }
    public string? ConnectionId { get; set; }
    public string? ConnectionName { get; set; }
    public string? Database { get; set; }
    public string Query { get; set; } = "";
    public double DurationMs { get; set; }
    public int Rows { get; set; }
    public QueryRunStatus Status { get; set; }
    public string? Error { get; set; }

    /// <summary>A favorite is kept when entries expire, when the history is full and when the history is cleared (unless you ask).</summary>
    public bool IsFavorite { get; set; }

    /// <summary>An optional title the user gave the query. It is searchable and survives running the query again.</summary>
    public string? Caption { get; set; }

    /// <summary>Labels the user gave the query, to group and find it.</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>The query on one line, for lists.</summary>
    [JsonIgnore]
    public string OneLine => string.Join(" ", Query.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim())).Trim();
}

/// <summary>What an import did.</summary>
public sealed record QueryHistoryImportResult(int Added, int Updated, int Skipped);

/// <summary>
/// The most recent queries run from the editor, newest first, saved as JSON next to the settings file.
/// Running the same query again on the same connection and database moves it to the top instead of adding a copy.
/// Entries can expire after a number of days (0 keeps them always), and the history can be exported and imported.
/// Queries can be marked as favorites: they never expire and are the last to go when the history is full.
/// </summary>
public sealed class QueryHistory
{
    public const int DefaultCapacity = 1000;
    public const string ExportFormat = "tickbird-query-history";
    private readonly string? path;
    private readonly Func<DateTime> utcNow;
    private readonly List<QueryHistoryEntry> entries;

    /// <param name="retentionDays">Entries older than this many days are removed; 0 keeps them always.</param>
    /// <param name="clock">The current UTC time (replaceable for tests).</param>
    public QueryHistory(string? path, int capacity = DefaultCapacity, int retentionDays = 0, Func<DateTime>? clock = null)
    {
        this.path = path;
        utcNow = clock ?? (() => DateTime.UtcNow);
        Capacity = Math.Max(1, capacity);
        RetentionDays = Math.Max(0, retentionDays);
        entries = Load(path);
        if (RemoveExpired() > 0) Save();
    }

    public int Capacity { get; }

    /// <summary>Entries older than this many days are removed. 0 means they never expire.</summary>
    public int RetentionDays { get; private set; }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<QueryHistoryEntry> Entries => entries;

    /// <summary>Raised after the history changes.</summary>
    public event Action? Changed;

    public static string PathNextTo(string settingsFile) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsFile)) ?? ".", "query-history.json");

    /// <summary>Changes how long entries are kept (0 = always) and removes the ones that are now too old.</summary>
    public void SetRetention(int days)
    {
        days = Math.Max(0, days);
        if (days == RetentionDays) return;
        RetentionDays = days;
        Prune();
    }

    /// <summary>Removes expired entries. Returns how many were removed.</summary>
    public int Prune()
    {
        var removed = RemoveExpired();
        if (removed > 0) SaveAndNotify();
        return removed;
    }

    private int RemoveExpired()
    {
        if (RetentionDays <= 0) return 0;
        var cutoff = utcNow().AddDays(-RetentionDays);
        return entries.RemoveAll(e => !e.IsFavorite && e.RunAtUtc < cutoff);
    }

    /// <summary>
    /// Drops the oldest entries that are not favorites until the history fits its capacity. The entry just added
    /// (<paramref name="keep"/>) is never dropped, so a full list of favorites can grow instead of losing a new query.
    /// </summary>
    private void TrimToCapacity(QueryHistoryEntry? keep = null)
    {
        while (entries.Count > Capacity)
        {
            var index = entries.FindLastIndex(e => !e.IsFavorite && !ReferenceEquals(e, keep));
            if (index < 0) break;
            entries.RemoveAt(index);
        }
    }

    /// <summary>How many entries are favorites.</summary>
    public int FavoriteCount => entries.Count(e => e.IsFavorite);

    private static List<string> MergeTags(IEnumerable<string>? a, IEnumerable<string>? b) =>
        CleanTags((a ?? Enumerable.Empty<string>()).Concat(b ?? Enumerable.Empty<string>()));

    /// <summary>Trims the tags, drops blanks and duplicates (ignoring case) and sorts them.</summary>
    public static List<string> CleanTags(IEnumerable<string>? tags) =>
        (tags ?? Enumerable.Empty<string>()).Select(t => t?.Trim() ?? "").Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Every tag in use, sorted.</summary>
    public IReadOnlyList<string> AllTags => CleanTags(entries.SelectMany(e => e.Tags ?? new List<string>()));

    /// <summary>Replaces the tags of these entries. Returns how many entries changed.</summary>
    public int SetTags(IEnumerable<string> ids, IEnumerable<string>? tags)
    {
        var set = ids.ToHashSet();
        var clean = CleanTags(tags);
        var changed = 0;
        foreach (var entry in entries.Where(e => set.Contains(e.Id) && !clean.SequenceEqual(e.Tags ?? new List<string>())))
        {
            entry.Tags = new List<string>(clean);
            changed++;
        }
        if (changed > 0) SaveAndNotify();
        return changed;
    }

    /// <summary>How many entries have a caption.</summary>
    public int CaptionedCount => entries.Count(e => !string.IsNullOrWhiteSpace(e.Caption));

    /// <summary>Sets the caption of an entry; null or blank removes it. Returns false when there is no such entry.</summary>
    public bool SetCaption(string id, string? caption)
    {
        var entry = entries.FirstOrDefault(e => e.Id == id);
        if (entry == null) return false;
        var trimmed = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim();
        if (entry.Caption == trimmed) return true;
        entry.Caption = trimmed;
        SaveAndNotify();
        return true;
    }

    /// <summary>Marks or unmarks entries as favorites. Returns how many entries changed.</summary>
    public int SetFavorite(IEnumerable<string> ids, bool favorite)
    {
        var set = ids.ToHashSet();
        var changed = 0;
        foreach (var entry in entries.Where(e => set.Contains(e.Id) && e.IsFavorite != favorite))
        {
            entry.IsFavorite = favorite;
            changed++;
        }
        if (changed > 0) SaveAndNotify();
        return changed;
    }

    /// <summary>
    /// Saves entries (all of them, or just the given ones) to a JSON file that <see cref="Import"/> can read
    /// on this or another computer.
    /// </summary>
    public int Export(string file, IEnumerable<QueryHistoryEntry>? selection = null)
    {
        var list = (selection ?? entries).ToList();
        var document = new { format = ExportFormat, version = 1, exportedAtUtc = utcNow(), entries = list };
        var directory = Path.GetDirectoryName(Path.GetFullPath(file));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(file, JsonConvert.SerializeObject(document, Formatting.Indented));
        return list.Count;
    }

    /// <summary>
    /// Merges the entries of an exported file into the history. A query that is already there (same connection,
    /// database and text) keeps the more recent run. Entries that are already expired are skipped.
    /// Throws <see cref="InvalidDataException"/> when the file is not a query history export.
    /// </summary>
    public QueryHistoryImportResult Import(string file)
    {
        List<QueryHistoryEntry>? incoming;
        try
        {
            var text = File.ReadAllText(file);
            var token = Newtonsoft.Json.Linq.JToken.Parse(text);
            var array = token is Newtonsoft.Json.Linq.JObject document ? document["entries"] : token;
            if (array is not Newtonsoft.Json.Linq.JArray) throw new InvalidDataException("The file does not contain query history entries.");
            incoming = array.ToObject<List<QueryHistoryEntry>>();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The file is not a valid query history export.", ex);
        }
        if (incoming == null) throw new InvalidDataException("The file does not contain query history entries.");

        int added = 0, updated = 0, skipped = 0;
        var cutoff = RetentionDays > 0 ? utcNow().AddDays(-RetentionDays) : DateTime.MinValue;
        foreach (var entry in incoming)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Query) || (entry.RunAtUtc < cutoff && !entry.IsFavorite)) { skipped++; continue; }
            entry.RunAtUtc = entry.RunAtUtc.ToUniversalTime();
            if (string.IsNullOrWhiteSpace(entry.Id)) entry.Id = Guid.NewGuid().ToString("N");
            var existing = entries.FirstOrDefault(e => e.Id == entry.Id || SameQuery(e, entry));
            if (existing == null) { entries.Add(entry); added++; }
            else if (existing.RunAtUtc < entry.RunAtUtc)
            {
                entry.IsFavorite |= existing.IsFavorite;
                if (string.IsNullOrWhiteSpace(entry.Caption)) entry.Caption = existing.Caption;
                entry.Tags = MergeTags(entry.Tags, existing.Tags);
                entries[entries.IndexOf(existing)] = entry;
                updated++;
            }
            else if ((entry.IsFavorite && !existing.IsFavorite) || (string.IsNullOrWhiteSpace(existing.Caption) && !string.IsNullOrWhiteSpace(entry.Caption)))
            {
                existing.IsFavorite |= entry.IsFavorite;
                if (string.IsNullOrWhiteSpace(existing.Caption)) existing.Caption = entry.Caption;
                updated++;
            }
            else skipped++;
        }
        if (added + updated > 0)
        {
            var ordered = entries.OrderByDescending(e => e.RunAtUtc).ToList();
            entries.Clear();
            entries.AddRange(ordered);
            TrimToCapacity();
            SaveAndNotify();
        }
        return new QueryHistoryImportResult(added, updated, skipped);
    }

    private static bool SameQuery(QueryHistoryEntry a, QueryHistoryEntry b) =>
        a.ConnectionId == b.ConnectionId
        && string.Equals(a.Database, b.Database, StringComparison.Ordinal)
        && string.Equals(a.Query.Trim(), b.Query.Trim(), StringComparison.Ordinal);

    public void Add(QueryHistoryEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Query)) return;
        RemoveExpired();
        // Running a favorite again keeps it a favorite.
        // The same goes for the caption.
        foreach (var same in entries.Where(e => SameQuery(e, entry)))
        {
            entry.IsFavorite |= same.IsFavorite;
            if (string.IsNullOrWhiteSpace(entry.Caption)) entry.Caption = same.Caption;
            entry.Tags = MergeTags(entry.Tags, same.Tags);
        }
        entries.RemoveAll(e => SameQuery(e, entry));
        entries.Insert(0, entry);
        TrimToCapacity(keep: entry);
        SaveAndNotify();
    }

    /// <summary>Removes the entries with these ids. Favorites among them are only removed when <paramref name="includeFavorites"/> is true.</summary>
    public void Remove(IEnumerable<string> ids, bool includeFavorites = true)
    {
        var set = ids.ToHashSet();
        if (entries.RemoveAll(e => set.Contains(e.Id) && (includeFavorites || !e.IsFavorite)) > 0) SaveAndNotify();
    }

    /// <summary>Removes every entry. Favorites are kept unless <paramref name="includeFavorites"/> is true.</summary>
    public void Clear(bool includeFavorites = true)
    {
        if (entries.RemoveAll(e => includeFavorites || !e.IsFavorite) > 0) SaveAndNotify();
    }

    /// <summary>
    /// Entries whose caption, tags, query, connection or database contain every word of <paramref name="text"/> (ignoring case),
    /// optionally only for one connection and/or only the favorites.
    /// </summary>
    public IReadOnlyList<QueryHistoryEntry> Search(string? text, string? connectionId = null, bool favoritesOnly = false)
    {
        var words = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return entries.Where(e => connectionId == null || e.ConnectionId == connectionId)
            .Where(e => !favoritesOnly || e.IsFavorite)
            .Where(e => words.All(w => e.Query.Contains(w, StringComparison.OrdinalIgnoreCase)
                                        || (e.Caption?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)
                                        || (e.Tags?.Any(t => t.Contains(w, StringComparison.OrdinalIgnoreCase)) ?? false)
                                        || (e.ConnectionName?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)
                                        || (e.Database?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)))
            .ToList();
    }

    private void SaveAndNotify()
    {
        Save();
        Changed?.Invoke();
    }

    private void Save()
    {
        if (path == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(entries, Formatting.Indented));
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException) { /* history is a convenience; never fail a query over it */ }
        catch (UnauthorizedAccessException) { }
    }

    private static List<QueryHistoryEntry> Load(string? path)
    {
        try
        {
            if (path == null || !File.Exists(path)) return new List<QueryHistoryEntry>();
            return JsonConvert.DeserializeObject<List<QueryHistoryEntry>>(File.ReadAllText(path))?
                .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Query)).ToList() ?? new List<QueryHistoryEntry>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new List<QueryHistoryEntry>();
        }
    }
}
