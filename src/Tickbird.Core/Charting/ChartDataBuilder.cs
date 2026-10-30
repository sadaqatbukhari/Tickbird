using System.Globalization;
using Tickbird.Core.Data;

namespace Tickbird.Core.Charting;

/// <summary>
/// Turns query results into chart lines. Has no UI dependency so it can be unit-tested.
/// <list type="bullet">
/// <item>Each numeric column becomes a line; text columns are skipped (and offered for "split by").</item>
/// <item>Each result series is kept apart, so an InfluxQL <c>GROUP BY tag</c> gives one line per tag set.</item>
/// <item>A result without a time column becomes a bar chart by category.</item>
/// </list>
/// </summary>
public static class ChartDataBuilder
{
    private static readonly string[] TimeColumnNames = { "time", "timestamp", "_time" };

    public static ChartData Build(IEnumerable<ResultSeries> results, ChartOptions? options = null)
    {
        options ??= new ChartOptions();
        var sources = (results ?? Enumerable.Empty<ResultSeries>())
            .Where(s => s != null && s.Columns.Count > 0).ToList();
        if (sources.Count == 0 || sources.All(s => s.Values.Count == 0)) return ChartData.Empty("The query returned no rows to chart.");

        // Column roles are decided across all result sets so combined charts agree on fields.
        var allColumns = sources.SelectMany(s => s.Columns).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var timeColumn = FindTimeColumn(sources);
        var numeric = new List<string>();
        var text = new List<string>();
        foreach (var column in allColumns)
        {
            if (string.Equals(column, timeColumn, StringComparison.OrdinalIgnoreCase)) continue;
            var cells = Cells(sources, column).Where(v => v != null).ToList();
            if (cells.Count == 0) continue;                                   // all null: nothing to say about it
            if (cells.All(ChartValues.IsNumeric)) numeric.Add(column);
            else text.Add(column);
        }

        if (numeric.Count == 0)
            return ChartData.Empty("There are no numeric columns to chart. Text columns: "
                                   + (text.Count == 0 ? "none" : string.Join(", ", text)) + ".", text);

        var splitCandidates = text.Where(c => DistinctCount(sources, c, 1000) is > 1 and <= 1000).ToList();
        var splitBy = options.SplitBy != null && text.Contains(options.SplitBy, StringComparer.OrdinalIgnoreCase)
            ? text.First(c => string.Equals(c, options.SplitBy, StringComparison.OrdinalIgnoreCase))
            : null;
        var visible = numeric.Where(f => !options.HiddenFields.Contains(f)).ToList();

        // InfluxQL aggregates without GROUP BY time stamp every row with the same instant (1970-01-01):
        // one point in time is better shown as bars, one per result set or row.
        var singleInstant = timeColumn != null && Cells(sources, timeColumn)
            .Select(v => ChartValues.TryGetUtc(v, out var t) ? t.Ticks : (long?)null)
            .Where(t => t != null).Distinct().Take(2).Count() < 2;

        return timeColumn != null && !singleInstant
            ? BuildTime(sources, timeColumn, numeric, visible, text, splitCandidates, splitBy, options)
            : BuildCategory(sources, numeric, visible, text, splitCandidates, options);
    }

    private static ChartData BuildTime(List<ResultSeries> sources, string timeColumn, List<string> numeric, List<string> visible,
        List<string> text, List<string> splitCandidates, string? splitBy, ChartOptions options)
    {
        var showName = sources.Select(s => s.Name ?? "").Distinct().Count() > 1;
        var lines = new List<ChartSeries>();
        var totalLines = 0;
        foreach (var source in sources)
        {
            var timeIndex = source.GetColumnIndex(timeColumn);
            if (timeIndex < 0) continue;
            var splitIndex = splitBy != null ? source.GetColumnIndex(splitBy) : -1;

            // (time, row) pairs with a readable timestamp, grouped by the split value.
            var groups = new Dictionary<string, List<(double X, IList<object> Row)>>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var row in source.Values)
            {
                if (row == null || timeIndex >= row.Count || !ChartValues.TryGetUtc(row[timeIndex], out var utc)) continue;
                var key = splitIndex >= 0 && splitIndex < row.Count ? Convert.ToString(row[splitIndex], CultureInfo.InvariantCulture) ?? "(null)" : "";
                if (!groups.TryGetValue(key, out var list))
                {
                    groups[key] = list = new List<(double, IList<object>)>();
                    order.Add(key);
                }
                list.Add((ChartValues.ToX(utc), row));
            }

            if (splitBy != null) order.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var key in order)
            {
                var points = groups[key];
                // Results are usually time-ordered, but ORDER BY time DESC and SQL queries may not be.
                if (!IsSorted(points)) points = points.OrderBy(p => p.X).ToList();
                foreach (var field in visible)
                {
                    var fieldIndex = source.GetColumnIndex(field);
                    if (fieldIndex < 0) continue;
                    totalLines++;
                    if (lines.Count >= options.MaxSeries) continue;
                    var xs = new double[points.Count];
                    var ys = new double[points.Count];
                    for (var i = 0; i < points.Count; i++)
                    {
                        xs[i] = points[i].X;
                        ys[i] = fieldIndex < points[i].Row.Count ? ChartValues.ToDouble(points[i].Row[fieldIndex]) : double.NaN;
                    }
                    var count = xs.Length;
                    (xs, ys) = Downsampler.Lttb(xs, ys, options.MaxPointsPerSeries);
                    lines.Add(new ChartSeries(Label(source, field, showName, splitBy, key), field, xs, ys, count));
                }
            }
        }

        string? message = null;
        if (lines.Count == 0 && visible.Count == 0) message = "All numeric columns are hidden. Choose at least one field.";
        else if (lines.Count == 0) message = $"No rows have a readable '{timeColumn}' value.";
        else if (totalLines > lines.Count) message = $"Showing the first {lines.Count} of {totalLines} lines. Narrow the query or choose a different split.";

        return new ChartData
        {
            AxisKind = lines.Count > 0 ? ChartAxisKind.Time : ChartAxisKind.None,
            Series = lines,
            Fields = numeric,
            SplitCandidates = splitCandidates,
            SkippedColumns = text,
            XColumn = timeColumn,
            Message = message
        };
    }

    private static ChartData BuildCategory(List<ResultSeries> sources, List<string> numeric, List<string> visible,
        List<string> text, List<string> splitCandidates, ChartOptions options)
    {
        // Label bars with the first text column; otherwise with the series tags or name; otherwise the row number.
        var labelColumn = text.FirstOrDefault();
        var showName = sources.Select(s => s.Name ?? "").Distinct().Count() > 1;
        var categories = new List<string>();
        var rows = new List<(ResultSeries Source, IList<object> Row)>();
        foreach (var source in sources)
        {
            var labelIndex = labelColumn != null ? source.GetColumnIndex(labelColumn) : -1;
            foreach (var row in source.Values)
            {
                if (row == null) continue;
                string label;
                if (labelIndex >= 0 && labelIndex < row.Count && row[labelIndex] != null)
                    label = Convert.ToString(row[labelIndex], CultureInfo.CurrentCulture) ?? "";
                else if (source.Tags.Count > 0 || (showName && !string.IsNullOrEmpty(source.Name)))
                    label = SeriesSuffix(source, showName).Trim();
                else
                    label = (categories.Count + 1).ToString(CultureInfo.CurrentCulture);
                categories.Add(label);
                rows.Add((source, row));
            }
        }

        string? message = null;
        if (rows.Count > options.MaxCategories)
        {
            message = $"Showing the first {options.MaxCategories} of {rows.Count} rows as bars. Add a time column (or GROUP BY time) for a line chart.";
            rows = rows.Take(options.MaxCategories).ToList();
            categories = categories.Take(options.MaxCategories).ToList();
        }
        if (visible.Count == 0) message = "All numeric columns are hidden. Choose at least one field.";

        var xs = Enumerable.Range(0, rows.Count).Select(i => (double)i).ToArray();
        var lines = visible.Select(field => new ChartSeries(field, field, xs,
            rows.Select(r => r.Source.GetColumnIndex(field) is var i and >= 0 && i < r.Row.Count ? ChartValues.ToDouble(r.Row[i]) : double.NaN).ToArray(),
            rows.Count)).ToList();

        return new ChartData
        {
            AxisKind = lines.Count > 0 ? ChartAxisKind.Category : ChartAxisKind.None,
            Series = lines,
            Fields = numeric,
            SplitCandidates = splitCandidates,
            SkippedColumns = text.Where(c => !string.Equals(c, labelColumn, StringComparison.OrdinalIgnoreCase)).ToList(),
            Categories = categories,
            XColumn = labelColumn,
            Message = message
        };
    }

    /// <summary>A column named time/timestamp, or else the first column whose values are all timestamps.</summary>
    private static string? FindTimeColumn(List<ResultSeries> sources)
    {
        foreach (var name in TimeColumnNames)
        {
            var column = sources.SelectMany(s => s.Columns).FirstOrDefault(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
            if (column != null && Cells(sources, column).Any(v => ChartValues.TryGetUtc(v, out _))) return column;
        }
        foreach (var column in sources.SelectMany(s => s.Columns).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var cells = Cells(sources, column).Where(v => v != null).Take(100).ToList();
            if (cells.Count > 0 && cells.All(v => v is DateTime or DateTimeOffset)) return column;
        }
        return null;
    }

    private static IEnumerable<object?> Cells(IEnumerable<ResultSeries> sources, string column)
    {
        foreach (var source in sources)
        {
            var index = source.GetColumnIndex(column);
            if (index < 0) continue;
            foreach (var row in source.Values)
                if (row != null && index < row.Count) yield return row[index];
        }
    }

    private static int DistinctCount(IEnumerable<ResultSeries> sources, string column, int stopAfter)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cell in Cells(sources, column))
        {
            seen.Add(Convert.ToString(cell, CultureInfo.InvariantCulture) ?? "");
            if (seen.Count > stopAfter) break;
        }
        return seen.Count;
    }

    private static bool IsSorted(List<(double X, IList<object> Row)> points)
    {
        for (var i = 1; i < points.Count; i++)
            if (points[i].X < points[i - 1].X) return false;
        return true;
    }

    private static string Label(ResultSeries source, string field, bool showName, string? splitBy, string splitValue)
    {
        var name = showName && !string.IsNullOrEmpty(source.Name) ? source.Name + "." + field : field;
        var parts = source.Tags.Select(t => $"{t.Key}={t.Value}").ToList();
        if (splitBy != null) parts.Add($"{splitBy}={splitValue}");
        return parts.Count == 0 ? name : $"{name} {{{string.Join(", ", parts)}}}";
    }

    private static string SeriesSuffix(ResultSeries source, bool showName)
    {
        var tags = string.Join(", ", source.Tags.Select(t => $"{t.Key}={t.Value}"));
        var name = showName ? source.Name ?? "" : "";
        return tags.Length == 0 ? name : $"{name} {{{tags}}}";
    }
}
