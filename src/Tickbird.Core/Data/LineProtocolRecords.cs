#nullable enable
using System.Globalization;
using System.Net;

namespace Tickbird.Core.Data;

/// <summary>
/// Editing records of the InfluxDB family. A record is a point: its time and tags identify it and its fields are the values.
/// Writing a point with the same measurement, tags and time replaces the fields it names, so an edit is a write; a delete is
/// InfluxQL's <c>DELETE ... WHERE time = ... AND tag = ...</c> (InfluxDB 1.x only).
/// </summary>
public static class LineProtocolRecords
{
    private static CommandResult Ok(string message = "OK") => new(message, HttpStatusCode.OK, true);
    private static CommandResult Failed(string message) => new(message, HttpStatusCode.BadRequest, false);

    public static async Task<RecordSchema> GetSchemaAsync(DatabaseClient client, string database, string measurement)
    {
        var columns = new List<RecordColumn> { new("time", "time", IsKey: true, IsTime: true) };
        foreach (var tag in await client.GetTagKeysAsync(database, measurement).ConfigureAwait(false))
            columns.Add(new RecordColumn(tag, "tag", IsKey: true));
        foreach (var field in await client.GetFieldKeysAsync(database, measurement).ConfigureAwait(false))
            if (!string.Equals(field.Name, "time", StringComparison.OrdinalIgnoreCase)) columns.Add(new RecordColumn(field.Name, field.Type ?? "string"));
        return new RecordSchema(measurement, columns, keyIsExact: true, keyIsFixed: true);
    }

    public static DataPoint BuildPoint(RecordSchema schema, IReadOnlyDictionary<string, object?> values, DateTime time)
    {
        var tags = new Dictionary<string, object>();
        var fields = new Dictionary<string, object>();
        foreach (var column in schema.Columns)
        {
            if (column.IsTime || !values.TryGetValue(column.Name, out var value) || value == null) continue;
            var text = value as string ?? RecordValues.ToText(value);
            if (text.Length == 0) continue;
            if (column.IsKey) tags[column.Name] = text;
            else fields[column.Name] = value is string ? RecordValues.ParseField(column.Name, text, column.DataType) : value;
        }
        if (fields.Count == 0) throw new InvalidOperationException("A point needs at least one field value.");
        return new DataPoint(schema.Table, tags, fields, time);
    }

    private static IReadOnlyDictionary<string, object?> Widen(IReadOnlyDictionary<string, object> values) =>
        values.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.OrdinalIgnoreCase);

    public static async Task<CommandResult> InsertAsync(DatabaseClient client, string database, RecordSchema schema, IReadOnlyDictionary<string, object> values)
    {
        try
        {
            var all = Widen(values);
            var time = all.TryGetValue("time", out var t) && t != null && RecordValues.ToText(t).Length > 0
                ? (t is DateTime d ? d : RecordValues.ParseTime(RecordValues.ToText(t))) : DateTime.UtcNow;
            return await client.WriteAsync(database, BuildPoint(schema, all, time)).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) { return Failed(ex.Message); }
    }

    public static async Task<CommandResult> UpdateAsync(DatabaseClient client, string database, RecordSchema schema, bool influxQl,
        IReadOnlyDictionary<string, object> original, IReadOnlyDictionary<string, object> changes)
    {
        try
        {
            var key = schema.Columns.Where(c => c.IsKey && changes.ContainsKey(c.Name)).Select(c => c.Name).ToList();
            if (key.Count > 0)
                return Failed($"The time and tags ({string.Join(", ", key)}) identify a point and cannot be changed. Insert a new point and delete this one instead.");
            var all = Widen(original);
            var time = TimeOf(original);
            if (await FindAsync(client, database, schema, original, influxQl).ConfigureAwait(false) is { } problem) return Failed(problem);
            // Only the edited fields are written: the point's other fields are left as they are.
            var edited = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in schema.Columns.Where(c => c.IsKey)) if (all.TryGetValue(column.Name, out var v)) edited[column.Name] = v;
            foreach (var pair in changes)
            {
                if (pair.Value == null || RecordValues.ToText(pair.Value).Length == 0)
                    return Failed($"InfluxDB cannot store an empty value for {pair.Key}. Delete the point if it is not wanted.");
                edited[pair.Key] = pair.Value;
            }
            return await client.WriteAsync(database, BuildPoint(schema, edited, time)).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) { return Failed(ex.Message); }
    }

    public static async Task<CommandResult> DeleteAsync(DatabaseClient client, string database, RecordSchema schema,
        IReadOnlyDictionary<string, object> original)
    {
        try
        {
            if (await FindAsync(client, database, schema, original, influxQl: true).ConfigureAwait(false) is { } problem) return Failed(problem);
            await client.QueryAsync(database, DeleteStatement(schema, original)).ConfigureAwait(false);
            return Ok();
        }
        catch (InvalidOperationException ex) { return Failed(ex.Message); }
    }

    public static DateTime TimeOf(IReadOnlyDictionary<string, object> original)
    {
        var value = original.FirstOrDefault(p => string.Equals(p.Key, "time", StringComparison.OrdinalIgnoreCase)).Value;
        return value switch
        {
            DateTime time => DateTime.SpecifyKind(time, DateTimeKind.Utc),
            DateTimeOffset offset => offset.UtcDateTime,
            string text => RecordValues.ParseTime(text),
            _ => throw new InvalidOperationException("The record has no time, so the point cannot be found. Include the time column in the query.")
        };
    }

    private static string Quote(string name) => "\"" + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string Literal(string value, bool influxQl) =>
        "'" + (influxQl ? value.Replace("\\", "\\\\").Replace("'", "\\'") : value.Replace("'", "''")) + "'";

    /// <summary>The WHERE condition that finds a point: its time and every tag (an absent tag is matched as empty).</summary>
    public static string Where(RecordSchema schema, IReadOnlyDictionary<string, object> original, bool influxQl)
    {
        var terms = new List<string> { "time = " + Literal(RecordValues.ToText(TimeOf(original)), influxQl) };
        foreach (var tag in schema.Columns.Where(c => c.IsKey && !c.IsTime))
        {
            var present = original.FirstOrDefault(p => string.Equals(p.Key, tag.Name, StringComparison.OrdinalIgnoreCase));
            if (present.Key == null) throw new InvalidOperationException($"The tag {tag.Name} is needed to find the point. Select it, or use SELECT *.");
            terms.Add($"{Quote(tag.Name)} = {Literal(RecordValues.ToText(present.Value), influxQl)}");
        }
        return string.Join(" AND ", terms);
    }

    public static string DeleteStatement(RecordSchema schema, IReadOnlyDictionary<string, object> original) =>
        $"DELETE FROM {Quote(schema.Table)} WHERE {Where(schema, original, influxQl: true)}";

    /// <summary>Checks the point exists (a time with more precision than was read would match nothing). Returns a message when it does not.</summary>
    private static async Task<string?> FindAsync(DatabaseClient client, string database, RecordSchema schema,
        IReadOnlyDictionary<string, object> original, bool influxQl)
    {
        var query = $"SELECT * FROM {Quote(schema.Table)} WHERE {Where(schema, original, influxQl)} LIMIT 1";
        var found = (await client.QueryAsync(database, query).ConfigureAwait(false) ?? Enumerable.Empty<ResultSeries>()).Any(s => s.Values.Count > 0);
        return found ? null : "The point was not found. It may have been changed or deleted since the query ran, or its time has more precision than was shown; run the query again.";
    }

    public static string Describe(object? value) => RecordValues.ToText(value).Length == 0 ? "(empty)" : RecordValues.ToText(value);

    public static string InvariantNumber(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
