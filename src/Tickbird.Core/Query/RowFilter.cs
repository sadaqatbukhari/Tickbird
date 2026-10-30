using System.Globalization;
using Tickbird.Core.Data;
using Tickbird.Core.Mapping;

namespace Tickbird.Core.Query;

/// <summary>
/// Filters the rows of a query result: a quick search (every word must appear in some cell, as displayed) and
/// conditions on columns joined by AND (speed &gt; 3, state = work...). The grid, the chart and the map all show
/// the rows that pass.
/// </summary>
public sealed class RowFilter
{
    /// <summary>Quick search text; each word must appear in at least one cell of the row (ignoring case).</summary>
    public string? Text { get; set; }

    /// <summary>Column conditions; all complete ones must hold.</summary>
    public List<MapCondition> Conditions { get; set; } = new();

    public bool IsActive => Words().Length > 0 || Conditions.Any(c => c.IsComplete);

    /// <summary>A short description for the status line, e.g. "speed > 3 and “north”".</summary>
    public string Describe()
    {
        var parts = Conditions.Where(c => c.IsComplete).Select(c => c.Describe()).ToList();
        if (Words().Length > 0) parts.Add($"“{Text!.Trim()}”");
        return string.Join(" and ", parts);
    }

    public RowFilter Copy() => new()
    {
        Text = Text,
        Conditions = Conditions.Select(c => new MapCondition { Column = c.Column, Operator = c.Operator, Value = c.Value }).ToList()
    };

    /// <summary>Indexes of the rows that pass. <paramref name="formatter"/> gives the displayed text for the quick search.</summary>
    public IReadOnlyList<int> MatchingRows(ResultSeries series, ResultFormatter? formatter = null)
    {
        var words = Words();
        var conditions = Conditions.Where(c => c.IsComplete).ToList();
        var columns = conditions.Select(c => IndexOf(series, c.Column!)).ToArray();
        var rows = new List<int>();
        for (var r = 0; r < series.Values.Count; r++)
        {
            var row = series.Values[r];
            var pass = true;
            for (var c = 0; c < conditions.Count && pass; c++)
                pass = conditions[c].Matches(columns[c] >= 0 && columns[c] < row.Count ? row[columns[c]] : null);
            if (pass && words.Length > 0) pass = words.All(w => RowContains(series, row, w, formatter));
            if (pass) rows.Add(r);
        }
        return rows;
    }

    /// <summary>The result with only the rows that pass (the same series when nothing is filtered).</summary>
    public ResultSeries Apply(ResultSeries series, ResultFormatter? formatter = null)
    {
        if (!IsActive) return series;
        var rows = MatchingRows(series, formatter);
        if (rows.Count == series.Values.Count) return series;
        return new ResultSeries(series.Name, series.Columns, series.Tags, rows.Select(r => series.Values[r]).ToList());
    }

    private string[] Words() => (Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static bool RowContains(ResultSeries series, IList<object> row, string word, ResultFormatter? formatter)
    {
        for (var c = 0; c < row.Count; c++)
        {
            var value = row[c];
            if (value == null) continue;
            var column = c < series.Columns.Count ? series.Columns[c] : "";
            var text = formatter?.Format(column, value) ?? Convert.ToString(value, CultureInfo.InvariantCulture);
            if (text != null && text.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
            // Also match the raw value (for example 52.1 when the grid shows 52,1).
            if (value is IFormattable f && f.ToString(null, CultureInfo.InvariantCulture).Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
        }
        // Tags of a GROUP BY result count as cells of every row.
        return series.Tags.Any(t => t.Value?.Contains(word, StringComparison.OrdinalIgnoreCase) == true);
    }

    private static int IndexOf(ResultSeries series, string column)
    {
        for (var i = 0; i < series.Columns.Count; i++)
            if (string.Equals(series.Columns[i], column, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}
