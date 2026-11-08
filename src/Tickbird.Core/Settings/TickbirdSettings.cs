using Tickbird.Core.Data;
using Newtonsoft.Json;

namespace Tickbird.Core.Settings;

/// <summary>
/// Persisted preferences and connections. Property names match the WinForms app's settings.json
/// so a file from 3.x can be read directly.
/// </summary>
public sealed class TickbirdSettings
{
    public const string TimeFormat12Hour = "hh:mm:ss tt";
    public const string TimeFormat24Hour = "HH:mm:ss";
    /// <summary>Use the short date format of the computer's regional settings (the default).</summary>
    public const string DateFormatSystem = "system";
    public const string DateFormatDay = "d/MM/yyyy";
    public const string DateFormatMonth = "M/dd/yyyy";
    /// <summary>German style, also used in Austria, Switzerland, Poland, Russia, Turkey and more. The dots are literal.</summary>
    public const string DateFormatGerman = "dd'.'MM'.'yyyy";
    /// <summary>Dutch style, with literal dashes.</summary>
    public const string DateFormatDutch = "dd'-'MM'-'yyyy";
    /// <summary>ISO 8601 (Sweden, Lithuania and others, and the usual choice for logs).</summary>
    public const string DateFormatIso = "yyyy'-'MM'-'dd";
    /// <summary>Day first with dots and no zero padding (Finland, Czechia, Slovakia).</summary>
    public const string DateFormatDotted = "d'.'M'.'yyyy";

    /// <summary>The date formats offered in Settings → Date Format, as (format, example, who uses it).</summary>
    public static readonly IReadOnlyList<(string Format, string Example, string Description)> DateFormats = new[]
    {
        (DateFormatSystem, "", "System default (your regional settings)"),
        (DateFormatMonth, "12/31/2016", "Month first (US)"),
        (DateFormatDay, "31/12/2016", "Day first (UK, France, Spain, Italy)"),
        (DateFormatGerman, "31.12.2016", "German (DE, AT, CH, PL, RU, TR)"),
        (DateFormatDotted, "31.12.2016", "Dots, no zero padding (FI, CZ, SK)"),
        (DateFormatDutch, "31-12-2016", "Dutch (NL)"),
        (DateFormatIso, "2016-12-31", "ISO 8601 (SE, LT, logs)"),
    };

    /// <summary>
    /// The .NET date pattern for a saved <see cref="DateFormat"/> value: the culture's short date pattern for
    /// <see cref="DateFormatSystem"/> (or a blank value), otherwise the value itself.
    /// </summary>
    public static string ResolveDateFormat(string? format, IFormatProvider? culture = null)
    {
        if (!string.IsNullOrWhiteSpace(format) && !string.Equals(format, DateFormatSystem, StringComparison.OrdinalIgnoreCase)) return format;
        var info = (culture as System.Globalization.CultureInfo ?? System.Globalization.CultureInfo.CurrentCulture).DateTimeFormat;
        return info.ShortDatePattern;
    }

    public int SchemaVersion { get; set; } = 1;
    public string? Version { get; set; }
    public string TimeFormat { get; set; } = TimeFormat12Hour;
    public string DateFormat { get; set; } = DateFormatSystem;
    public bool AllowUntrustedSsl { get; set; }
    public List<DatabaseConnection> Connections { get; set; } = new();

    /// <summary>
    /// Connection groups (for example PROD, DEV). Saved separately from the connections so a
    /// group that has no connections yet is not lost.
    /// </summary>
    public List<string> Groups { get; set; } = new();

    /// <summary>Object Explorer docking layout (docked side, auto-hide, floating window bounds).</summary>
    public PaneLayout Explorer { get; set; } = new();

    /// <summary>View > Toolbar.</summary>
    public bool ShowToolbar { get; set; } = true;

    /// <summary>View > Status Bar.</summary>
    public bool ShowStatusBar { get; set; } = true;

    /// <summary>Queries run from the editor are stopped after this many seconds; 0 means no limit.</summary>
    public int QueryTimeoutSeconds { get; set; } = 300;

    /// <summary>Record every query run from the editor in the query history.</summary>
    public bool KeepQueryHistory { get; set; } = true;

    /// <summary>Show the Getting Started guide when Tickbird starts (Help › Getting Started opens it any time).</summary>
    public bool ShowGettingStarted { get; set; } = true;

    /// <summary>The order of the Query History columns, left to right, by header. Empty means the default order.</summary>
    public List<string> QueryHistoryColumnOrder { get; set; } = new();

    /// <summary>Query history entries older than this many days are removed. 0 (the default) keeps them always.</summary>
    public int QueryHistoryRetentionDays { get; set; }

    /// <summary>Trims a group name; blank means "no group" and returns null.</summary>
    public static string? NormalizeGroup(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    /// <summary>True when both names refer to the same (non-blank) group, ignoring case.</summary>
    public static bool SameGroup(string? a, string? b)
    {
        var left = NormalizeGroup(a);
        return left != null && string.Equals(left, NormalizeGroup(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>All groups: saved groups plus any group only referenced by a connection, distinct and sorted.</summary>
    public IReadOnlyList<string> AllGroups()
    {
        var names = new List<string>();
        foreach (var name in (Groups ?? new List<string>()).Concat(Connections.Where(c => c != null).Select(c => c.Group)))
        {
            var group = NormalizeGroup(name);
            if (group != null && !names.Any(n => SameGroup(n, group))) names.Add(group);
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>Returns the existing group's name (with its saved casing), or null if there is no such group.</summary>
    public string? FindGroup(string? name) => AllGroups().FirstOrDefault(g => SameGroup(g, name));

    /// <summary>Makes sure the group is saved. Returns its canonical name, or null for "no group".</summary>
    public string? EnsureGroup(string? name)
    {
        var group = FindGroup(name) ?? NormalizeGroup(name);
        if (group == null) return null;
        Groups ??= new List<string>();
        if (!Groups.Any(g => SameGroup(g, group))) Groups.Add(group);
        return group;
    }

    /// <summary>Renames a group and moves its connections to the new name.</summary>
    public void RenameGroup(string oldName, string newName)
    {
        var target = NormalizeGroup(newName) ?? throw new ArgumentException("Group name cannot be blank.", nameof(newName));
        Groups ??= new List<string>();
        Groups.RemoveAll(g => SameGroup(g, oldName));
        foreach (var connection in Connections.Where(c => c != null && SameGroup(c.Group, oldName))) connection.Group = target;
        EnsureGroup(target);
    }

    /// <summary>Deletes a group. Its connections are kept and become ungrouped.</summary>
    public void RemoveGroup(string name)
    {
        Groups?.RemoveAll(g => SameGroup(g, name));
        foreach (var connection in Connections.Where(c => c != null && SameGroup(c.Group, name))) connection.Group = null;
    }

    public int CountInGroup(string name) => Connections.Count(c => c != null && SameGroup(c.Group, name));

    /// <summary>Adds groups from an imported file that are not already present.</summary>
    public void MergeGroups(IEnumerable<string>? incoming)
    {
        if (incoming == null) return;
        foreach (var group in incoming) EnsureGroup(group);
    }

    /// <summary>Adds connections not already present (matched by ID); returns how many were added.</summary>
    public int MergeConnections(IEnumerable<DatabaseConnection> incoming)
    {
        var added = 0;
        foreach (var connection in incoming)
        {
            if (connection == null) continue;
            if (string.IsNullOrWhiteSpace(connection.Id)) connection.Id = Guid.NewGuid().ToString();
            if (Connections.Any(c => c.Id == connection.Id)) continue;
            Connections.Add(connection);
            added++;
        }
        return added;
    }
}

/// <summary>Reads a WinForms-era settings.json without modifying it.</summary>
public static class LegacySettingsImporter
{
    public static string DefaultWindowsPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CymaticLabs", "InfluxDB Studio", "settings.json");

    public static TickbirdSettings? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonConvert.DeserializeObject<TickbirdSettings>(File.ReadAllText(path));
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }
}
