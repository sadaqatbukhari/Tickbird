using System.Text;
using Tickbird.Core.Data;
using Tickbird.Core.Navigation;
using Newtonsoft.Json;

namespace Tickbird.Core.Query;

/// <summary>CSV / JSON export and clipboard text for query results.</summary>
public static class ResultExporter
{
    public static string EscapeCsv(string? value) => Escape(value, new[] { ',', '"', '\r', '\n' });

    public static string EscapeTabSeparated(string? value) => Escape(value, new[] { '\t', '"', '\r', '\n' });

    private static string Escape(string? value, char[] specials)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.IndexOfAny(specials) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    public static void WriteCsv(TextWriter writer, ResultSeries result, ResultFormatter formatter, ISet<int>? onlyRows = null)
    {
        writer.WriteLine(string.Join(",", result.Columns.Select(EscapeCsv)));
        for (var rowIndex = 0; rowIndex < result.Values.Count; rowIndex++)
        {
            if (onlyRows != null && !onlyRows.Contains(rowIndex)) continue;
            var row = result.Values[rowIndex];
            var values = new string[result.Columns.Count];
            for (var c = 0; c < values.Length; c++)
                values[c] = EscapeCsv(formatter.Format(result.Columns[c], c < row.Count ? row[c] : null));
            writer.WriteLine(string.Join(",", values));
        }
    }

    public static void WriteJson(TextWriter writer, ResultSeries result, ISet<int>? onlyRows = null)
    {
        var serializer = JsonSerializer.CreateDefault();
        using var json = new JsonTextWriter(writer) { Formatting = Formatting.Indented, CloseOutput = false };
        json.WriteStartArray();
        for (var rowIndex = 0; rowIndex < result.Values.Count; rowIndex++)
        {
            if (onlyRows != null && !onlyRows.Contains(rowIndex)) continue;
            var row = result.Values[rowIndex];
            var item = new Dictionary<string, object?>();
            for (var c = 0; c < result.Columns.Count && c < row.Count; c++)
                item[result.Columns[c]] = row[c];
            serializer.Serialize(json, item);
        }
        json.WriteEndArray();
    }

    /// <summary>Tab-separated text with a header row, as used by Copy Row(s).</summary>
    public static string ToTabSeparated(ResultSeries result, ResultFormatter formatter, IEnumerable<int> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join("\t", result.Columns.Select(EscapeTabSeparated)));
        foreach (var rowIndex in rows.Distinct().Where(i => i >= 0 && i < result.Values.Count))
        {
            var row = result.Values[rowIndex];
            builder.AppendLine(string.Join("\t", result.Columns.Select((name, c) =>
                EscapeTabSeparated(formatter.Format(name, c < row.Count ? row[c] : null)))));
        }
        return builder.ToString().TrimEnd('\r', '\n');
    }

    public static string SafeFileName(string fileName)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(invalid, '_');
        return fileName;
    }

    /// <summary>CSV for simple list tables (series, tag keys, fields, ...).</summary>
    public static void WriteCsv(TextWriter writer, TableData table, ISet<int>? onlyRows = null)
    {
        writer.WriteLine(string.Join(",", table.Columns.Select(EscapeCsv)));
        for (var i = 0; i < table.Rows.Count; i++)
        {
            if (onlyRows != null && !onlyRows.Contains(i)) continue;
            writer.WriteLine(string.Join(",", table.Rows[i].Select(EscapeCsv)));
        }
    }

    public static void WriteJson(TextWriter writer, TableData table, ISet<int>? onlyRows = null)
    {
        var serializer = JsonSerializer.CreateDefault();
        using var json = new JsonTextWriter(writer) { Formatting = Formatting.Indented, CloseOutput = false };
        json.WriteStartArray();
        for (var i = 0; i < table.Rows.Count; i++)
        {
            if (onlyRows != null && !onlyRows.Contains(i)) continue;
            var item = new Dictionary<string, object?>();
            for (var c = 0; c < table.Columns.Count && c < table.Rows[i].Count; c++)
                item[table.Columns[c]] = table.Rows[i][c];
            serializer.Serialize(json, item);
        }
        json.WriteEndArray();
    }
}
