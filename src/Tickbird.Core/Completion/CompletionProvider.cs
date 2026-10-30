using System.Text.RegularExpressions;
using System.Xml.Linq;
using Tickbird.Core.Data;
using Tickbird.Core.Providers;

namespace Tickbird.Core.Completion;

public enum CompletionKind { Keyword, Function, Measurement, Field, Tag, TagValue }

/// <summary>The caret is in the value of <c>tag = '...</c>; <see cref="Start"/> is where the value (or its quote) begins.</summary>
public sealed record TagValueContext(string Tag, int Start, string Prefix);

/// <summary>Where the caret is, which decides what is offered first.</summary>
public enum CompletionContext { Any, Measurement, Column }

public sealed record CompletionItem(string Text, string Description)
{
    public CompletionKind Kind { get; init; } = Description == "Function" ? CompletionKind.Function : CompletionKind.Keyword;

    /// <summary>Text inserted into the editor (identifiers that need it are quoted).</summary>
    public string InsertText { get; init; } = Text;
}

/// <summary>
/// Framework-independent query completion (port of InfluxQueryIntellisense): keywords/functions,
/// measurements and field/tag names. The whole database schema can be supplied up front; the measurement
/// named in the query's FROM clause is also loaded on demand.
/// </summary>
public sealed class CompletionProvider
{
    private const string InfluxQlResourceName = "Tickbird.Core.Resources.InfluxQL.xml";

    private static readonly Lazy<CompletionItem[]> InfluxQlItems = new(LoadInfluxQlItems);

    // InfluxDB 3 (SQL) keywords and common functions.
    private static readonly CompletionItem[] SqlItems = new[]
    {
        "SELECT", "FROM", "WHERE", "GROUP BY", "ORDER BY", "LIMIT", "OFFSET", "AS",
        "AND", "OR", "NOT", "IN", "IS NULL", "IS NOT NULL", "ASC", "DESC", "DISTINCT",
        "BETWEEN", "LIKE", "ILIKE", "HAVING", "JOIN", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "ON", "USING",
        "UNION", "UNION ALL", "WITH", "CASE", "WHEN", "THEN", "ELSE", "END", "INTERVAL", "NULL", "TRUE", "FALSE",
        "SHOW", "SHOW TABLES", "SHOW COLUMNS", "QUERIES", "SHARD", "SHARDS", "EXPLAIN", "EXPLAIN ANALYZE",
        "INFORMATION_SCHEMA", "TABLES", "COLUMNS",
        "COUNT()", "AVG()", "SUM()", "MIN()", "MAX()", "MEDIAN()", "STDDEV()", "VARIANCE()", "APPROX_PERCENTILE_CONT()",
        "FIRST_VALUE()", "LAST_VALUE()", "SELECTOR_FIRST()", "SELECTOR_LAST()", "SELECTOR_MIN()", "SELECTOR_MAX()",
        "DATE_BIN()", "DATE_BIN_GAPFILL()", "LOCF()", "INTERPOLATE()", "DATE_TRUNC()", "DATE_PART()", "EXTRACT()",
        "NOW()", "TO_TIMESTAMP()", "TO_TIMESTAMP_SECONDS()", "TO_TIMESTAMP_MILLIS()", "CAST()", "COALESCE()", "NULLIF()",
        "ABS()", "ROUND()", "CEIL()", "FLOOR()", "POWER()", "SQRT()", "LN()", "LOG10()",
        "CONCAT()", "LOWER()", "UPPER()", "LENGTH()", "SUBSTR()", "REPLACE()", "TRIM()"
    }.Select(v => v.EndsWith("()") ? new CompletionItem(v, "Function") : new CompletionItem(v, "SQL keyword")).ToArray();

    private static readonly Regex PlainIdentifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private readonly HashSet<string> measurements = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CompletionItem[]> schemaByMeasurement = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> attempted = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Measurement, string Tag), string[]> tagValues = new();

    /// <summary>Most values shown for one tag, so very high-cardinality tags stay responsive.</summary>
    public const int MaxTagValues = 1000;

    public IReadOnlyCollection<string> Measurements => measurements;
    public int FieldCount => schemaByMeasurement.Values.Sum(items => items.Count(i => i.Kind == CompletionKind.Field));
    public int TagCount => schemaByMeasurement.Values.Sum(items => items.Count(i => i.Kind == CompletionKind.Tag));

    /// <summary>Adds a whole database schema (all measurements with their fields and tags).</summary>
    public void SetSchema(IEnumerable<MeasurementSchema>? schema)
    {
        if (schema == null) return;
        foreach (var measurement in schema)
        {
            if (string.IsNullOrWhiteSpace(measurement.Name)) continue;
            measurements.Add(measurement.Name);
            schemaByMeasurement[measurement.Name] = SchemaItems(measurement.Name, measurement.Fields, measurement.Tags);
        }
    }

    // ------------------------------------------------------------------ tag values

    /// <summary>The tag's name as stored in the schema (matched ignoring case), or null if no measurement has that tag.</summary>
    public string? FindTag(string name) => schemaByMeasurement.Values.SelectMany(items => items)
        .Where(i => i.Kind == CompletionKind.Tag)
        .Select(i => i.Text)
        .OrderBy(t => string.Equals(t, name, StringComparison.Ordinal) ? 0 : 1)
        .FirstOrDefault(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Measurements that have the tag (used when the query has no FROM yet).</summary>
    public IReadOnlyList<string> MeasurementsWithTag(string tag) => schemaByMeasurement
        .Where(pair => pair.Value.Any(i => i.Kind == CompletionKind.Tag && i.Text == tag))
        .Select(pair => pair.Key)
        .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>The measurements whose values are offered: the FROM measurement if it has the tag, else every measurement with it (up to 10).</summary>
    public IReadOnlyList<string> TagValueSources(string? measurement, string tag)
    {
        var withTag = MeasurementsWithTag(tag);
        var from = measurement == null ? null : withTag.FirstOrDefault(m => string.Equals(m, measurement, StringComparison.OrdinalIgnoreCase));
        return from != null ? new[] { from } : withTag.Take(10).ToList();
    }

    public bool HasTagValues(string measurement, string tag) => tagValues.ContainsKey((measurement, tag));

    /// <summary>Keeps the loaded values with their tag.</summary>
    public void SetTagValues(string measurement, string tag, IEnumerable<string>? values) =>
        tagValues[(measurement, tag)] = (values ?? Enumerable.Empty<string>()).Where(v => v != null).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>Values of the tag from the given measurements, quoted as string literals, sorted and capped.</summary>
    public IReadOnlyList<CompletionItem> GetTagValueItems(InfluxDbServerVersion? version, IEnumerable<string> measurements, string tag) =>
        GetTagValueItems(version == null ? null : DatabaseProfiles.For(version), measurements, tag);

    /// <summary>Values of the tag from the given measurements, quoted for the database's query language, sorted and capped.</summary>
    public IReadOnlyList<CompletionItem> GetTagValueItems(DatabaseProfile? profile, IEnumerable<string> measurements, string tag) =>
        measurements.SelectMany(m => tagValues.TryGetValue((m, tag), out var values) ? values : Array.Empty<string>())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .Take(MaxTagValues)
            .Select(v => QuoteValue(v, profile))
            .Select(q => new CompletionItem(q, $"Value of tag {tag}") { Kind = CompletionKind.TagValue })
            .ToList();

    /// <summary>A string literal: InfluxQL escapes quotes with a backslash, SQL doubles them.</summary>
    public static string QuoteValue(string value, InfluxDbServerVersion? version) =>
        QuoteValue(value, version == null ? null : DatabaseProfiles.For(version));

    public static string QuoteValue(string value, DatabaseProfile? profile) =>
        profile?.IsSql == true
            ? "'" + value.Replace("'", "''") + "'"
            : "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    private static readonly Regex TagComparison = new(
        "(?:\"(?<quoted>[^\"]+)\"|(?<plain>[A-Za-z_][A-Za-z0-9_]*))\\s*(?:=|!=|<>)\\s*(?<value>'[^']*)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Finds <c>tag = </c>, <c>tag != '</c> or <c>tag = 'abc</c> right before the caret (outside comments).
    /// Returns null when the caret is not in a tag value.
    /// </summary>
    public static TagValueContext? FindTagValueContext(string textBeforeCaret, string lineBeforeCaret)
    {
        if (CompletionTrigger.InComment(lineBeforeCaret)) return null;
        var match = TagComparison.Match(textBeforeCaret);
        if (!match.Success) return null;
        var tag = match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["plain"].Value;
        var value = match.Groups["value"];
        return value.Success
            ? new TagValueContext(tag, value.Index, value.Value[1..])
            : new TagValueContext(tag, textBeforeCaret.Length, "");
    }

    /// <summary>True when the caret is in a WHERE condition (after WHERE, AND or OR).</summary>
    public static bool IsFilterContext(string? textBeforeCaret)
    {
        if (string.IsNullOrWhiteSpace(textBeforeCaret)) return false;
        var text = Regex.Replace(textBeforeCaret, "\"?[A-Za-z0-9_]*$", "");
        var matches = Regex.Matches(text, "\\b(SELECT|FROM|INTO|WHERE|AND|OR|BY|LIMIT|OFFSET|FILL|ORDER|GROUP|SLIMIT|TZ)\\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return matches.Count > 0 && matches[^1].Value.ToUpperInvariant() is "WHERE" or "AND" or "OR";
    }

    public IEnumerable<CompletionItem> GetItems(InfluxDbServerVersion? version, string? queryText) =>
        GetItems(version, queryText, null);

    public IEnumerable<CompletionItem> GetItems(InfluxDbServerVersion? version, string? queryText, string? textBeforeCaret) =>
        GetItems(version == null ? null : DatabaseProfiles.For(version), queryText, textBeforeCaret);

    /// <summary>The keywords and functions of a database: its query language plus the functions of the database itself.</summary>
    private static IReadOnlyList<CompletionItem> StandardItems(DatabaseProfile? profile)
    {
        var baseItems = profile?.IsSql == true ? SqlItems : InfluxQlItems.Value;
        if (profile == null || profile.CompletionExtras.Count == 0) return baseItems;
        return baseItems.Concat(profile.CompletionExtras.Select(e => new CompletionItem(e.Text, e.Detail)
            { Kind = e.IsFunction ? CompletionKind.Function : CompletionKind.Keyword })).ToList();
    }

    /// <summary>
    /// Items for the completion list, best first for the caret position: after FROM/INTO measurements come
    /// first; after SELECT/WHERE/AND/OR/BY the fields and tags of the FROM measurement come first.
    /// </summary>
    public IEnumerable<CompletionItem> GetItems(DatabaseProfile? profile, string? queryText, string? textBeforeCaret)
    {
        var standard = StandardItems(profile);
        var keywords = standard.Where(i => i.Kind == CompletionKind.Keyword);
        var functions = standard.Where(i => i.Kind == CompletionKind.Function);
        var measurementItems = measurements.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => new CompletionItem(n, "Measurement") { Kind = CompletionKind.Measurement, InsertText = Quote(n, profile) });

        var current = FindMeasurement(queryText);
        var currentColumns = current != null && schemaByMeasurement.TryGetValue(current, out var s) ? s : Array.Empty<CompletionItem>();
        var otherColumns = schemaByMeasurement
            .Where(pair => current == null || !string.Equals(pair.Key, current, StringComparison.OrdinalIgnoreCase))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .SelectMany(pair => pair.Value);
        var columns = currentColumns.Concat(otherColumns).Select(i => i with { InsertText = Quote(i.Text, profile) });

        var ordered = DetectContext(textBeforeCaret) switch
        {
            CompletionContext.Measurement => measurementItems.Concat(keywords).Concat(functions).Concat(columns),
            CompletionContext.Column => columns.Concat(functions).Concat(keywords).Concat(measurementItems),
            _ => keywords.Concat(functions).Concat(measurementItems).Concat(columns),
        };
        return ordered.GroupBy(i => i.Text, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    public async Task LoadMeasurementsAsync(DatabaseClient? client, string? database)
    {
        if (client == null || string.IsNullOrWhiteSpace(database)) return;
        foreach (var name in await client.GetMeasurementNamesAsync(database) ?? Enumerable.Empty<string>())
            if (!string.IsNullOrWhiteSpace(name)) measurements.Add(name);
    }

    /// <summary>Loads fields/tags of the query's FROM measurement if they are not known yet (tried once per measurement).</summary>
    public async Task LoadContextSchemaAsync(DatabaseClient? client, string? database, string? queryText)
    {
        var measurement = FindMeasurement(queryText);
        if (client == null || string.IsNullOrWhiteSpace(database) || measurement == null
            || schemaByMeasurement.ContainsKey(measurement) || !attempted.Add(measurement)) return;
        var fieldsTask = client.GetFieldKeysAsync(database, measurement);
        var tagsTask = client.GetTagKeysAsync(database, measurement);
        await Task.WhenAll(fieldsTask, tagsTask);
        schemaByMeasurement[measurement] = SchemaItems(measurement,
            (await fieldsTask ?? Enumerable.Empty<FieldKey>()).ToList(), (await tagsTask ?? Enumerable.Empty<string>()).ToList());
    }

    public static string? FindMeasurement(string? queryText)
    {
        if (string.IsNullOrWhiteSpace(queryText)) return null;
        var matches = Regex.Matches(queryText,
            "\\bFROM\\s+(?:\"(?<quoted>[^\"]+)\"|(?<plain>[A-Za-z_][\\w.:-]*))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (matches.Count == 0) return null;
        var match = matches[^1];
        return match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["plain"].Value;
    }

    /// <summary>Decides the completion context from the text before the caret (the word being typed is ignored).</summary>
    public static CompletionContext DetectContext(string? textBeforeCaret)
    {
        if (string.IsNullOrWhiteSpace(textBeforeCaret)) return CompletionContext.Any;
        var text = Regex.Replace(textBeforeCaret, "\"?[A-Za-z0-9_]*$", "");
        var matches = Regex.Matches(text, "\\b(SELECT|FROM|INTO|WHERE|AND|OR|BY|LIMIT|OFFSET|FILL|ORDER|GROUP|SLIMIT|TZ)\\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (matches.Count == 0) return CompletionContext.Any;
        return matches[^1].Value.ToUpperInvariant() switch
        {
            "FROM" or "INTO" => CompletionContext.Measurement,
            "SELECT" or "WHERE" or "AND" or "OR" or "BY" => CompletionContext.Column,
            _ => CompletionContext.Any,
        };
    }

    /// <summary>Quotes an identifier when it is not a plain word (or, for SQL, not lower case).</summary>
    public static string Quote(string name, InfluxDbServerVersion? version) =>
        Quote(name, version == null ? null : DatabaseProfiles.For(version));

    public static string Quote(string name, DatabaseProfile? profile)
    {
        var sql = profile?.IsSql == true;
        var plain = PlainIdentifier.IsMatch(name) && !IsReserved(name, profile)
                    && (!sql || string.Equals(name, name.ToLowerInvariant(), StringComparison.Ordinal));
        if (plain) return name;
        return "\"" + (sql ? name.Replace("\"", "\"\"") : name.Replace("\"", "\\\"")) + "\"";
    }

    private static bool IsReserved(string name, DatabaseProfile? profile) =>
        StandardItems(profile)
            .Any(i => i.Kind == CompletionKind.Keyword && string.Equals(i.Text, name, StringComparison.OrdinalIgnoreCase));

    private static CompletionItem[] SchemaItems(string measurement, IEnumerable<FieldKey> fields, IEnumerable<string> tags) =>
        fields.Where(f => !string.IsNullOrWhiteSpace(f.Name))
            .Select(f => new CompletionItem(f.Name, $"Field ({f.Type}) in {measurement}") { Kind = CompletionKind.Field })
            .Concat(tags.Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => new CompletionItem(t, $"Tag in {measurement}") { Kind = CompletionKind.Tag }))
            .ToArray();

    /// <summary>InfluxQL keywords used for highlighting, extracted from the shared language definition.</summary>
    public static IReadOnlyList<CompletionItem> InfluxQlKeywords => InfluxQlItems.Value;

    private static CompletionItem[] LoadInfluxQlItems()
    {
        using var stream = typeof(CompletionProvider).Assembly.GetManifestResourceStream(InfluxQlResourceName)
            ?? throw new InvalidOperationException("The embedded InfluxQL configuration could not be found.");
        var document = XDocument.Load(stream);
        return document.Descendants()
            .Where(e => e.Name.LocalName == "Lexem")
            .Select(e => new { Text = (string?)e.Attribute("BeginBlock"), Format = (string?)e.Attribute("FormatName") })
            .Where(i => !string.IsNullOrWhiteSpace(i.Text))
            .Select(i => string.Equals(i.Format, "Function", StringComparison.OrdinalIgnoreCase)
                ? new CompletionItem(i.Text + "()", "Function")
                : new CompletionItem(i.Text!, "Keyword"))
            .GroupBy(i => i.Text, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();
    }
}

/// <summary>When the completion list opens automatically, and which text it replaces.</summary>
public static class CompletionTrigger
{
    private static readonly Regex KeywordBeforeSpace = new("\\b(SELECT|FROM|INTO|WHERE|AND|OR|BY|ON)\\s$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Start of the word at the caret, including an opening double quote.</summary>
    public static int WordStart(string text, int caret)
    {
        var start = Math.Clamp(caret, 0, text.Length);
        while (start > 0 && IsWordChar(text[start - 1])) start--;
        if (start > 0 && text[start - 1] == '"') start--;
        return start;
    }

    /// <summary>True when the caret is inside a -- comment on the current line.</summary>
    public static bool InComment(string lineBeforeCaret)
    {
        bool single = false, dbl = false;
        for (var i = 0; i < lineBeforeCaret.Length; i++)
        {
            var c = lineBeforeCaret[i];
            if (!single && !dbl && c == '-' && i + 1 < lineBeforeCaret.Length && lineBeforeCaret[i + 1] == '-') return true;
            if (c == '\'' && !dbl) single = !single;
            else if (c == '"' && !single) dbl = !dbl;
        }
        return false;
    }

    /// <summary>True inside a -- comment or an unterminated string / quoted identifier on the current line.</summary>
    public static bool InCommentOrString(string lineBeforeCaret)
    {
        bool single = false, dbl = false;
        for (var i = 0; i < lineBeforeCaret.Length; i++)
        {
            var c = lineBeforeCaret[i];
            if (!single && !dbl && c == '-' && i + 1 < lineBeforeCaret.Length && lineBeforeCaret[i + 1] == '-') return true;
            if (c == '\'' && !dbl) single = !single;
            else if (c == '"' && !single) dbl = !dbl;
        }
        return single;   // inside "quoted identifier" we still want measurement/field suggestions
    }

    /// <summary>
    /// Whether to open the list (when it is not already open) after <paramref name="typed"/> was entered: while a
    /// word that starts with a letter is being typed, after a space that follows SELECT/FROM/WHERE/AND/OR/BY/INTO/ON,
    /// or after a comma in a column/measurement list.
    /// </summary>
    public static bool ShouldOpen(string textBeforeCaret, string lineBeforeCaret, string typed)
    {
        if (string.IsNullOrEmpty(typed) || InCommentOrString(lineBeforeCaret)) return false;
        var c = typed[^1];
        if (char.IsLetter(c) || c == '_')
        {
            var start = WordStart(textBeforeCaret, textBeforeCaret.Length);
            var word = textBeforeCaret[start..].TrimStart('"');
            return word.Length > 0 && (char.IsLetter(word[0]) || word[0] == '_');
        }
        if (c == ' ') return KeywordBeforeSpace.IsMatch(textBeforeCaret);
        if (c == ',') return CompletionProvider.DetectContext(textBeforeCaret) != CompletionContext.Any;
        return false;
    }
}
