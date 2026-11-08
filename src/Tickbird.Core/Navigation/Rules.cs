using Tickbird.Core.Data;

namespace Tickbird.Core.Navigation;

/// <summary>Input validation shared by the dialogs (ported from the WinForms dialogs; null means valid).</summary>
public static class Validation
{
    public const string IntervalHint = "It should be an InfluxDB time interval value such as: 1d, 2h, 10m, 30s, etc.";
    public const string SubQueryPlaceholder = "#edit your query / downsamplers here";

    public static string? RetentionPolicy(string? name, string? duration)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Retention Policy name cannot be blank.";
        if (string.IsNullOrWhiteSpace(duration)) return "Duration cannot be blank. " + IntervalHint;
        if (!InfluxDbHelper.IsTimeIntervalValid(duration.Trim())) return "Duration value is invalid. " + IntervalHint;
        return null;
    }

    public static string? User(string? name, string? password)
    {
        if (string.IsNullOrWhiteSpace(name)) return "User name cannot be blank.";
        if (string.IsNullOrWhiteSpace(password)) return "Password cannot be blank.";
        return null;
    }

    /// <summary>Validates a connection group name. <paramref name="current"/> is the name being renamed, if any.</summary>
    public static string? Group(string? name, IEnumerable<string>? existing, string? current = null)
    {
        var group = name?.Trim();
        if (string.IsNullOrEmpty(group)) return "Group name cannot be blank.";
        if (group.Length > 64) return "Group name cannot be longer than 64 characters.";
        if (existing != null && existing.Any(g => string.Equals(g, group, StringComparison.OrdinalIgnoreCase)
                                                  && !string.Equals(g, current, StringComparison.OrdinalIgnoreCase)))
            return $"A group named '{group}' already exists.";
        return null;
    }

    /// <summary>
    /// Type-to-confirm check for destructive actions: the typed text must match the name exactly
    /// (case-sensitive, because InfluxDB names are case-sensitive).
    /// </summary>
    public static bool NameMatches(string? typed, string? expected) =>
        !string.IsNullOrEmpty(expected) && string.Equals(typed, expected, StringComparison.Ordinal);

    public static string? Database(string? name, IEnumerable<string>? existing)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Database name cannot be blank.";
        if (existing != null && existing.Contains(name)) return $"A database named '{name}' already exists.";
        return null;
    }

    public static string? ContinuousQuery(string? name, string? destination, string? source, string? interval,
        bool resample, string? resampleEvery, string? resampleFor, string? subQuery)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Continuous Query name cannot be blank.";
        if (string.IsNullOrWhiteSpace(destination)) return "Destination cannot be blank.";
        if (string.IsNullOrWhiteSpace(source)) return "Source cannot be blank.";
        var intervalError = Interval(interval, "Interval");
        if (intervalError != null) return intervalError;
        if (resample)
        {
            if (!string.IsNullOrWhiteSpace(resampleEvery) && !InfluxDbHelper.IsTimeIntervalValid(resampleEvery.Trim()))
                return "Resample Every interval value is invalid. " + IntervalHint;
            if (!string.IsNullOrWhiteSpace(resampleFor) && !InfluxDbHelper.IsTimeIntervalValid(resampleFor.Trim()))
                return "Resample For interval value is invalid. " + IntervalHint;
        }
        return SubQuery(subQuery);
    }

    public static string? Backfill(string? destination, string? source, string? interval, DateTime from, DateTime to, string? subQuery)
    {
        if (string.IsNullOrWhiteSpace(destination)) return "Destination cannot be blank.";
        if (string.IsNullOrWhiteSpace(source)) return "Source cannot be blank.";
        var intervalError = Interval(interval, "Interval");
        if (intervalError != null) return intervalError;
        if (from > to) return "'From Time' is later than 'To Time'. 'From Time' should come before 'To Time'.";
        return SubQuery(subQuery);
    }

    private static string? Interval(string? interval, string label)
    {
        if (string.IsNullOrWhiteSpace(interval)) return $"{label} cannot be blank. " + IntervalHint;
        if (!InfluxDbHelper.IsTimeIntervalValid(interval.Trim())) return $"{label} value is invalid. " + IntervalHint;
        return null;
    }

    private static string? SubQuery(string? subQuery) =>
        string.IsNullOrWhiteSpace(subQuery) || subQuery == SubQueryPlaceholder ? "SubQuery cannot be blank." : null;

    /// <summary>Splits a comma-separated list, trimming entries; returns null when empty.</summary>
    public static List<string>? SplitList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var items = text.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        return items.Count > 0 ? items : null;
    }
}

public static class RetentionPolicyRules
{
    /// <summary>After the default policy is dropped/unset, pick which policy should become the default.</summary>
    public static RetentionPolicy? ChooseFallbackDefault(IEnumerable<RetentionPolicy> policies, string excludedName)
    {
        var list = policies.ToList();
        return list.FirstOrDefault(p => p.Name.Equals("autogen", StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(p => p.Name != excludedName);
    }

    /// <summary>Default policy first, as shown in the Fields retention policy dropdown.</summary>
    public static List<RetentionPolicy> OrderForFields(IEnumerable<RetentionPolicy> policies) =>
        policies.OrderByDescending(p => p.Default).ToList();
}

public sealed record TableData(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string?>> Rows);

public static class SeriesTable
{
    /// <summary>Turns "m,host=a,region=b" series keys into a table with one column per tag key.</summary>
    public static TableData Parse(string measurement, IEnumerable<string> seriesNames)
    {
        var columns = new List<string>();
        var parsed = new List<Dictionary<string, string>>();
        foreach (var seriesName in seriesNames)
        {
            var values = new Dictionary<string, string>();
            parsed.Add(values);
            foreach (var pair in seriesName.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', StringSplitOptions.RemoveEmptyEntries);
                if (kv.Length < 2 || kv[0] == measurement) continue;
                if (!columns.Contains(kv[0])) columns.Add(kv[0]);
                values[kv[0]] = kv[1];
            }
        }
        var rows = parsed.Select(v => (IReadOnlyList<string?>)columns.Select(c => v.TryGetValue(c, out var x) ? x : null).ToList()).ToList();
        return new TableData(columns, rows);
    }
}
