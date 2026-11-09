#nullable enable
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Tickbird.Core.Data;

/// <summary>A column of a table or measurement, as seen by the record editor.</summary>
/// <param name="IsKey">Part of what identifies a record: the primary key of a table, or the time and tags of a measurement.</param>
public sealed record RecordColumn(string Name, string DataType, bool IsKey = false, bool IsTime = false);

/// <summary>The columns of one table or measurement and which of them identify a record.</summary>
public sealed class RecordSchema
{
    public RecordSchema(string table, IReadOnlyList<RecordColumn> columns, bool keyIsExact, bool keyIsFixed = false)
    {
        Table = table;
        Columns = columns;
        KeyIsExact = keyIsExact;
        KeyIsFixed = keyIsFixed;
    }

    /// <summary>True when the key columns cannot be edited (a point's time and tags are what it is); a new record has to be inserted instead.</summary>
    public bool KeyIsFixed { get; }

    public string Table { get; }

    public IReadOnlyList<RecordColumn> Columns { get; }

    /// <summary>
    /// True when the key columns identify a single record (a primary key, or time and tags). False when the table has no key,
    /// so a record is found by matching every column and several identical rows can match.
    /// </summary>
    public bool KeyIsExact { get; }

    /// <summary>The columns that find a record: the key when there is one, otherwise every column.</summary>
    public IEnumerable<RecordColumn> Identity => KeyIsExact ? Columns.Where(c => c.IsKey) : Columns;

    public RecordColumn? Find(string name) => Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Why a record read from a query result cannot be changed or deleted, or null when it can: the result must hold every
    /// column that identifies the record.
    /// </summary>
    public string? WhyNotIdentifiable(IEnumerable<string> resultColumns)
    {
        var have = new HashSet<string>(resultColumns, StringComparer.OrdinalIgnoreCase);
        var missing = Identity.Where(c => !have.Contains(c.Name)).Select(c => c.Name).ToList();
        if (missing.Count == 0) return null;
        return KeyIsExact
            ? $"The result does not include the key column(s) {string.Join(", ", missing)}. Select them, or use SELECT *."
            : "This table has no primary key, so the whole record is needed to find it. Use SELECT * to edit or delete records.";
    }
}

/// <summary>The values of a record by column name; null is an empty (NULL) value.</summary>
public sealed class RecordValues : Dictionary<string, object?>
{
    public RecordValues() : base(StringComparer.OrdinalIgnoreCase) { }

    public RecordValues(IEnumerable<KeyValuePair<string, object?>> values) : base(values, StringComparer.OrdinalIgnoreCase) { }

    /// <summary>Text shown in an edit box for a value: no quotes, times in ISO form, and nothing for NULL.</summary>
    public static string ToText(object? value) => value switch
    {
        null => "",
        DateTime time => time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture),
        DateTimeOffset time => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    /// <summary>True when two values are the same record value (so an unchanged edit box is not sent).</summary>
    public static bool SameValue(object? original, string? edited) =>
        string.Equals(ToText(original), edited ?? "", StringComparison.Ordinal);

    /// <summary>
    /// Reads text typed by the user as a value of an InfluxDB field type (float, integer, boolean, string; or the SQL names
    /// InfluxDB 3 uses). Throws a message the user can read when the text does not fit.
    /// </summary>
    public static object ParseField(string name, string text, string? dataType)
    {
        var type = (dataType ?? "").ToLowerInvariant();
        try
        {
            if (type.Contains("bool")) return bool.Parse(text.Trim());
            if (type.Contains("int")) return long.Parse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (type.Contains("float") || type.Contains("double") || type.Contains("decimal") || type.Contains("real"))
                return double.Parse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        catch (FormatException) { throw new InvalidOperationException($"\"{text}\" is not a valid {dataType} value for {name}."); }
        catch (OverflowException) { throw new InvalidOperationException($"\"{text}\" is too large for {name}."); }
        return text;
    }

    /// <summary>Reads a time typed by the user (ISO 8601, with or without a zone; no zone means UTC).</summary>
    public static DateTime ParseTime(string text)
    {
        if (DateTime.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time))
            return DateTime.SpecifyKind(time, DateTimeKind.Utc);
        throw new InvalidOperationException($"\"{text}\" is not a valid time. Use a form like 2024-05-01T12:30:00Z.");
    }
}

/// <summary>Finds which table or measurement a query reads, so its rows can be edited.</summary>
public static class RecordQuery
{
    private static readonly Regex Comments = new(@"--[^\r\n]*|/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);

    // A name part is "quoted" (with "" for a quote) or a plain word; a name is one to three parts joined by dots.
    private const string Part = "(?:\"(?:[^\"]|\"\")+\"|[A-Za-z_][A-Za-z0-9_$]*)";
    private static readonly Regex Select = new(
        $@"^\s*SELECT\s+(?<columns>.+?)\s+FROM\s+(?<name>{Part}(?:\s*\.\s*{Part}){{0,2}})\s*(?<rest>.*?);?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    // Anything after the table that makes the rows something other than whole records of that table.
    private static readonly Regex NotPlain = new(
        @"\b(JOIN|UNION|INTERSECT|EXCEPT|GROUP\s+BY|HAVING|DISTINCT|INTO|WINDOW)\b|\bFROM\b|[,(]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Aggregate = new(
        @"\b(count|sum|mean|avg|min|max|median|mode|spread|stddev|first|last|percentile|derivative|difference|integral|distinct|date_trunc|date_bin|time_bucket)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The table named by a plain <c>SELECT ... FROM table [WHERE ...] [ORDER BY ...] [LIMIT ...]</c>, as written (a schema may
    /// come first), or null when the query is anything else: several statements, joins, grouping, aggregates, sub-queries.
    /// </summary>
    public static string? FindTable(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var text = Comments.Replace(query, " ").Trim();
        text = text.TrimEnd(';').Trim();
        if (text.Contains(';')) return null;          // more than one statement
        var match = Select.Match(text);
        if (!match.Success) return null;
        var columns = match.Groups["columns"].Value;
        if (Aggregate.IsMatch(columns) || columns.Contains('(') || Regex.IsMatch(columns, @"^\s*DISTINCT\b", RegexOptions.IgnoreCase)) return null;
        // The WHERE clause may hold anything (functions, IN lists); only what changes the shape of the result matters.
        var rest = match.Groups["rest"].Value;
        if (Regex.IsMatch(rest, @"\bGROUP\s+BY\b|\bJOIN\b|\bUNION\b", RegexOptions.IgnoreCase)) return null;
        var shapeOnly = Regex.Replace(rest, @"\bWHERE\b.*?(?=\bORDER\s+BY\b|\bLIMIT\b|\bOFFSET\b|\bSLIMIT\b|\bSOFFSET\b|$)", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        shapeOnly = Regex.Replace(shapeOnly, @"\bORDER\s+BY\b.*?(?=\bLIMIT\b|\bOFFSET\b|\bSLIMIT\b|\bSOFFSET\b|$)", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (NotPlain.IsMatch(shapeOnly)) return null;
        return Normalize(match.Groups["name"].Value);
    }

    private static string Normalize(string name) => Regex.Replace(name, @"\s*\.\s*", ".");

    /// <summary>Splits a name written in a query into its parts, without the quotes.</summary>
    public static IReadOnlyList<string> Parts(string name)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < name.Length && name[i + 1] == '"') { current.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else current.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == '.') { parts.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        parts.Add(current.ToString());
        return parts;
    }
}
