using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace Tickbird.Core.Query;

/// <summary>A query the user saved to reuse in any query tab.</summary>
public sealed class QuerySnippet
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Query { get; set; } = "";
}

/// <summary>Saved query snippets, kept as JSON next to the settings file (query-snippets.json).</summary>
public sealed class QuerySnippets
{
    private readonly string? path;
    private readonly List<QuerySnippet> items;

    public QuerySnippets(string? path)
    {
        this.path = path;
        items = Load(path);
    }

    /// <summary>Sorted by name.</summary>
    public IReadOnlyList<QuerySnippet> Items => items.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public event Action? Changed;

    public static string PathNextTo(string settingsFile) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsFile)) ?? ".", "query-snippets.json");

    /// <summary>Saves a snippet; a snippet with the same name (ignoring case) is replaced.</summary>
    public QuerySnippet Save(string name, string query)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("A snippet needs a name.", nameof(name));
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("A snippet needs a query.", nameof(query));
        var snippet = items.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (snippet == null) items.Add(snippet = new QuerySnippet { Name = name });
        snippet.Query = query.Trim();
        SaveAndNotify();
        return snippet;
    }

    public bool Rename(string id, string name)
    {
        name = name.Trim();
        var snippet = items.FirstOrDefault(s => s.Id == id);
        if (snippet == null || name.Length == 0) return false;
        if (items.Any(s => s.Id != id && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))) return false;
        snippet.Name = name;
        SaveAndNotify();
        return true;
    }

    public bool Remove(string id)
    {
        if (items.RemoveAll(s => s.Id == id) == 0) return false;
        SaveAndNotify();
        return true;
    }

    private void SaveAndNotify()
    {
        if (path != null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(items, Formatting.Indented));
                File.Move(temp, path, overwrite: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        Changed?.Invoke();
    }

    private static List<QuerySnippet> Load(string? path)
    {
        try
        {
            if (path == null || !File.Exists(path)) return new List<QuerySnippet>();
            return JsonConvert.DeserializeObject<List<QuerySnippet>>(File.ReadAllText(path))?
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.Name)).ToList() ?? new List<QuerySnippet>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new List<QuerySnippet>();
        }
    }
}

/// <summary>
/// Parameters in a query are written as <c>{{name}}</c>. Before the query runs, the user fills in a value for each
/// name and the placeholders are replaced by the text typed.
/// </summary>
public static class QueryParameters
{
    private static readonly Regex Placeholder = new(@"\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}", RegexOptions.Compiled);

    /// <summary>The distinct parameter names in the order they first appear.</summary>
    public static IReadOnlyList<string> Find(string query) =>
        Placeholder.Matches(query).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Replaces every placeholder with its value; a name without a value is left as it is.</summary>
    public static string Apply(string query, IReadOnlyDictionary<string, string> values) =>
        Placeholder.Replace(query, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
}
