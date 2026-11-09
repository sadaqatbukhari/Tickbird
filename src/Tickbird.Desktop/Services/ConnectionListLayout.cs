using Tickbird.Core.Data;
using Tickbird.Core.Providers;
using Tickbird.Core.Settings;

namespace Tickbird.Desktop.Services;

public enum ConnectionListEntryKind { TypeHeading, GroupHeading, Connection }

/// <summary>One line of the Manage Connections list: a heading or a connection.</summary>
public sealed record ConnectionListEntry(ConnectionListEntryKind Kind, string Title, int Count = 0, DatabaseConnection? Connection = null, DatabaseKind? Database = null);

/// <summary>
/// Decides how the Manage Connections list is arranged. Connections are grouped by database type first and by group
/// second. A level is left out when it would only repeat itself: there are no type headings when every connection uses
/// the same database, and no group headings under a type whose connections have no group.
/// </summary>
public static class ConnectionListLayout
{
    public const string NoGroup = "No group";

    public static bool Matches(DatabaseConnection connection, string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        var haystack = string.Join(' ', connection.Name, connection.Host, connection.Port, TickbirdSettings.NormalizeGroup(connection.Group), connection.Profile.DisplayName);
        return search.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(word => haystack.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<ConnectionListEntry> Build(IEnumerable<DatabaseConnection> all, string? search = null)
    {
        var everything = all.Where(c => c != null).ToList();
        // The type level depends on all connections, so the list does not change shape while the user types a search.
        var showTypes = everything.Select(c => c.Kind).Distinct().Count() > 1;
        var shown = everything.Where(c => Matches(c, search)).ToList();

        var entries = new List<ConnectionListEntry>();
        foreach (var kind in shown.Select(c => c.Kind).Distinct().OrderBy(k => (int)k))
        {
            var ofKind = shown.Where(c => c.Kind == kind).ToList();
            if (showTypes) entries.Add(new ConnectionListEntry(ConnectionListEntryKind.TypeHeading, ofKind[0].Profile.DisplayName, ofKind.Count, null, kind));

            var anyGroup = ofKind.Any(c => TickbirdSettings.NormalizeGroup(c.Group) != null);
            if (!anyGroup)
            {
                entries.AddRange(ofKind.Select(Row));
                continue;
            }

            // Named groups alphabetically, then the connections without a group; saved order is kept inside a group.
            var named = ofKind.Where(c => TickbirdSettings.NormalizeGroup(c.Group) != null)
                .GroupBy(c => TickbirdSettings.NormalizeGroup(c.Group)!, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var group in named)
            {
                entries.Add(new ConnectionListEntry(ConnectionListEntryKind.GroupHeading, group.Key, group.Count(), null, kind));
                entries.AddRange(group.Select(Row));
            }
            var ungrouped = ofKind.Where(c => TickbirdSettings.NormalizeGroup(c.Group) == null).ToList();
            if (ungrouped.Count > 0)
            {
                entries.Add(new ConnectionListEntry(ConnectionListEntryKind.GroupHeading, NoGroup, ungrouped.Count, null, kind));
                entries.AddRange(ungrouped.Select(Row));
            }
        }
        return entries;
    }

    private static ConnectionListEntry Row(DatabaseConnection c) => new(ConnectionListEntryKind.Connection, c.Name, 0, c);
}
