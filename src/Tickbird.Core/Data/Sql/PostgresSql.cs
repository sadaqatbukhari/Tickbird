using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Tickbird.Core.Data.Sql;

/// <summary>Quoting and small builders for PostgreSQL-flavoured SQL (PostgreSQL, TimescaleDB, CrateDB, QuestDB...).</summary>
public static class PostgresSql
{
    /// <summary>A quoted identifier: <c>"my table"</c>, with quotes inside doubled.</summary>
    public static string Identifier(string name)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("An identifier cannot be empty.", nameof(name));
        return "\"" + name.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>
    /// Splits <c>schema.table</c> into its parts. A name without a dot is in the <c>public</c> schema. Quoted parts
    /// (<c>"my.schema"."my.table"</c>) may contain dots.
    /// </summary>
    public static (string Schema, string Table) SplitQualified(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A table name cannot be empty.", nameof(name));
        var parts = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c == '"')
            {
                if (quoted && i + 1 < name.Length && name[i + 1] == '"') { current.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (c == '.' && !quoted)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else current.Append(c);
        }
        parts.Add(current.ToString());
        return parts.Count switch
        {
            1 => ("public", parts[0]),
            2 => (parts[0], parts[1]),
            _ => (parts[^2], parts[^1])
        };
    }

    /// <summary>The quoted, schema-qualified name of a table: <c>"public"."cpu"</c>.</summary>
    public static string Table(string name)
    {
        var (schema, table) = SplitQualified(name);
        return Identifier(schema) + "." + Identifier(table);
    }

    /// <summary>The name shown in the tree: <c>cpu</c> for the <c>public</c> schema, otherwise <c>schema.cpu</c>.</summary>
    public static string DisplayName(string schema, string table) =>
        string.Equals(schema, "public", StringComparison.Ordinal) ? table : schema + "." + table;

    /// <summary>A string literal: <c>'it''s'</c>. Backslashes switch to the escape syntax so the value is the same on every server setting.</summary>
    public static string Literal(string? value)
    {
        if (value == null) return "NULL";
        var escaped = value.Replace("'", "''");
        return value.Contains('\\') ? "E'" + escaped.Replace("\\", "\\\\") + "'" : "'" + escaped + "'";
    }

    private static readonly Regex Duration = new(@"^\s*(?<n>\d+(?:\.\d+)?)\s*(?<u>ns|us|µs|ms|s|m|h|d|w)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// A PostgreSQL interval literal body for a duration written the InfluxDB way (<c>30d</c>, <c>1w</c>, <c>12h</c>,
    /// <c>90m</c>, <c>500ms</c>) or already as text (<c>30 days</c>, <c>1 mon</c>), for example <c>30 days</c>.
    /// Throws <see cref="FormatException"/> for an empty value or one with characters that cannot be part of an interval.
    /// </summary>
    public static string Interval(string? duration)
    {
        if (string.IsNullOrWhiteSpace(duration)) throw new FormatException("A duration is required, for example 7d or 12h.");
        var match = Duration.Match(duration);
        if (match.Success)
        {
            var number = match.Groups["n"].Value;
            var unit = match.Groups["u"].Value.ToLowerInvariant() switch
            {
                "ns" => null,
                "us" or "µs" => "microseconds",
                "ms" => "milliseconds",
                "s" => "seconds",
                "m" => "minutes",
                "h" => "hours",
                "d" => "days",
                _ => "weeks"
            };
            if (unit == null)
            {
                var micro = decimal.Parse(number, CultureInfo.InvariantCulture) / 1000m;
                return micro.ToString(CultureInfo.InvariantCulture) + " microseconds";
            }
            return number + " " + unit;
        }
        var text = duration.Trim();
        // Text such as "30 days" or "1 mon" or "01:30:00": it starts with a number and has only letters, digits and a few signs.
        if (!Regex.IsMatch(text, @"^[+-]?\d[0-9A-Za-z .:+\-]*$"))
            throw new FormatException($"'{duration}' is not a valid duration. Use for example 7d, 12h or 30 days.");
        return text;
    }

    /// <summary>An INTERVAL literal: <c>INTERVAL '30 days'</c>.</summary>
    public static string IntervalLiteral(string? duration) => "INTERVAL " + Literal(Interval(duration));

    /// <summary>True for a column type that holds text (and so is shown as a tag).</summary>
    public static bool IsTextType(string? dataType) =>
        dataType != null && (dataType.StartsWith("character", StringComparison.OrdinalIgnoreCase)
                             || dataType.Equals("text", StringComparison.OrdinalIgnoreCase)
                             || dataType.Equals("name", StringComparison.OrdinalIgnoreCase)
                             || dataType.Equals("uuid", StringComparison.OrdinalIgnoreCase)
                             || dataType.Equals("citext", StringComparison.OrdinalIgnoreCase)
                             || dataType.Equals("USER-DEFINED", StringComparison.OrdinalIgnoreCase));

    /// <summary>True for a column type that holds a point in time.</summary>
    public static bool IsTimestampType(string? dataType) =>
        dataType != null && dataType.StartsWith("timestamp", StringComparison.OrdinalIgnoreCase);
}
