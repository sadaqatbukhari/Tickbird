using System.Globalization;

namespace Tickbird.Core.Data;

/// <summary>Field and tag names of one measurement (used for query IntelliSense).</summary>
public sealed class MeasurementSchema
{
    public MeasurementSchema(string name, IReadOnlyList<FieldKey> fields, IReadOnlyList<string> tags)
    {
        Name = name;
        Fields = fields;
        Tags = tags;
    }

    public string Name { get; }
    public IReadOnlyList<FieldKey> Fields { get; }
    public IReadOnlyList<string> Tags { get; }

    /// <summary>
    /// Builds the schema from InfluxDB 1.x <c>SHOW FIELD KEYS</c> / <c>SHOW TAG KEYS</c> results
    /// (one series per measurement) plus the measurement list, so measurements without fields still appear.
    /// </summary>
    public static IReadOnlyList<MeasurementSchema> FromInfluxQl(
        IEnumerable<ResultSeries> fieldSeries, IEnumerable<ResultSeries> tagSeries, IEnumerable<string>? measurements = null)
    {
        var builder = new Builder();
        foreach (var name in measurements ?? Enumerable.Empty<string>()) builder.Measurement(name);
        foreach (var series in fieldSeries)
        {
            var key = series.GetColumnIndex("fieldKey");
            var type = series.GetColumnIndex("fieldType");
            builder.Measurement(series.Name);
            if (key < 0) continue;
            foreach (var row in series.Values)
                builder.Field(series.Name, Text(row, key), type < 0 ? null : Text(row, type));
        }
        foreach (var series in tagSeries)
        {
            var key = series.GetColumnIndex("tagKey");
            builder.Measurement(series.Name);
            if (key < 0) continue;
            foreach (var row in series.Values) builder.Tag(series.Name, Text(row, key));
        }
        return builder.Build();
    }

    /// <summary>
    /// Builds the schema from an InfluxDB 3.x <c>information_schema.columns</c> query with the columns
    /// table_name, column_name and data_type. Dictionary-typed columns are tags; <c>time</c> is skipped.
    /// </summary>
    public static IReadOnlyList<MeasurementSchema> FromInformationSchema(IEnumerable<ResultSeries> series)
    {
        var builder = new Builder();
        foreach (var item in series)
        {
            var table = item.GetColumnIndex("table_name");
            var column = item.GetColumnIndex("column_name");
            var type = item.GetColumnIndex("data_type");
            if (table < 0 || column < 0) continue;
            foreach (var row in item.Values)
            {
                var measurement = Text(row, table);
                var name = Text(row, column);
                var dataType = type < 0 ? null : Text(row, type);
                builder.Measurement(measurement);
                if (string.Equals(name, "time", StringComparison.OrdinalIgnoreCase)) continue;
                if (dataType != null && dataType.Contains("Dictionary", StringComparison.OrdinalIgnoreCase)) builder.Tag(measurement, name);
                else builder.Field(measurement, name, dataType);
            }
        }
        return builder.Build();
    }

    private static string? Text(IList<object> row, int index) =>
        index < row.Count ? Convert.ToString(row[index], CultureInfo.InvariantCulture) : null;

    private sealed class Builder
    {
        private readonly Dictionary<string, (List<FieldKey> Fields, List<string> Tags)> items = new(StringComparer.Ordinal);

        public void Measurement(string? name)
        {
            if (!string.IsNullOrWhiteSpace(name) && !items.ContainsKey(name)) items[name] = (new(), new());
        }

        public void Field(string? measurement, string? name, string? type)
        {
            if (string.IsNullOrWhiteSpace(measurement) || string.IsNullOrWhiteSpace(name)) return;
            Measurement(measurement);
            var fields = items[measurement].Fields;
            if (!fields.Any(f => f.Name == name)) fields.Add(new FieldKey(name, string.IsNullOrWhiteSpace(type) ? "unknown" : type));
        }

        public void Tag(string? measurement, string? name)
        {
            if (string.IsNullOrWhiteSpace(measurement) || string.IsNullOrWhiteSpace(name)) return;
            Measurement(measurement);
            var tags = items[measurement].Tags;
            if (!tags.Contains(name)) tags.Add(name);
        }

        public IReadOnlyList<MeasurementSchema> Build() => items
            .OrderBy(i => i.Key, StringComparer.OrdinalIgnoreCase)
            .Select(i => new MeasurementSchema(i.Key, i.Value.Fields, i.Value.Tags))
            .ToList();
    }
}
