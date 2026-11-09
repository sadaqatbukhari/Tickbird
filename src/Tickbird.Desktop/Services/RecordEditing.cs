using System.Text;
using Avalonia.Controls;
using Tickbird.Core.Data;
using Tickbird.Core.Providers;
using Tickbird.Desktop.Views;

namespace Tickbird.Desktop.Services;

/// <summary>
/// Inserting, changing and deleting one record of a query result. Every change is shown to the user and confirmed before it is
/// sent, and a change or delete only goes ahead when exactly one record matches (see the client's record methods).
/// </summary>
public static class RecordEditing
{
    /// <summary>Which of the three operations a client supports (the buttons shown for a result).</summary>
    public static DatabaseCapabilities Supported(DatabaseClient? client) =>
        (client?.Connection.Profile.Capabilities ?? DatabaseCapabilities.None) & DatabaseCapabilities.AllRecordOperations;

    private const int MaxLines = 14;

    /// <summary>The lines of a confirmation: one "column = value" per column (the rest summarised).</summary>
    public static string Lines(IEnumerable<KeyValuePair<string, string>> items)
    {
        var all = items.ToList();
        var text = new StringBuilder();
        foreach (var item in all.Take(MaxLines)) text.AppendLine($"  {item.Key} = {Shorten(item.Value)}");
        if (all.Count > MaxLines) text.AppendLine($"  … and {all.Count - MaxLines} more columns");
        return text.ToString().TrimEnd();
    }

    private static string Shorten(string value) => value.Length == 0 ? "(empty)" : value.Length > 80 ? value[..80] + "…" : value;

    private static async Task<RecordSchema?> SchemaAsync(Window owner, DatabaseClient client, string database, string table)
    {
        try { return await client.GetRecordSchemaAsync(database, table); }
        catch (Exception ex)
        {
            await Dialogs.ShowErrorAsync(owner, ex.Message, "Cannot edit " + table);
            return null;
        }
    }

    private static async Task<bool> RunAsync(Window owner, string title, Func<Task<CommandResult>> action)
    {
        try
        {
            var result = await action();
            if (result.Success) return true;
            await Dialogs.ShowErrorAsync(owner, result.Body, title + " failed");
        }
        catch (Exception ex) { await Dialogs.ShowErrorAsync(owner, ex.Message, title + " failed"); }
        return false;
    }

    /// <summary>The values typed in a form, as what the client receives: text, or null for an empty box.</summary>
    private static Dictionary<string, object> ToValues(IEnumerable<KeyValuePair<string, string?>> typed) =>
        typed.ToDictionary(p => p.Key, p => (object)p.Value!, StringComparer.OrdinalIgnoreCase);

    /// <summary>Asks for a new record, confirms it, and inserts it. Returns true when a record was inserted.</summary>
    public static async Task<bool> InsertAsync(Window owner, DatabaseClient client, string database, string table)
    {
        var schema = await SchemaAsync(owner, client, database, table);
        if (schema == null) return false;
        var form = new RecordEditorWindow($"Insert record into {table}",
            "Type the values of the new record. Leave a box empty to leave that column out.",
            schema, schema.Columns.Select(c => c.Name), null, "Insert");
        if (!await form.ShowAsync(owner)) return false;
        var typed = form.Values.Where(p => p.Value != null).ToList();
        if (typed.Count == 0)
        {
            await Dialogs.ShowMessageAsync(owner, "Insert record", "Enter a value for at least one column.");
            return false;
        }
        var message = $"Insert this record into {table}?\n\n{Lines(typed.Select(p => new KeyValuePair<string, string>(p.Key, p.Value!)))}";
        var (confirmed, _) = await Dialogs.ConfirmWithOptionAsync(owner, "Confirm Insert", message, null, "Insert");
        if (!confirmed) return false;
        var values = ToValues(typed);
        return await RunAsync(owner, "Insert", () => client.InsertRecordAsync(database, schema, values));
    }

    /// <summary>Shows the record in a form, confirms what changed, and saves it. Returns true when the record was changed.</summary>
    public static async Task<bool> EditAsync(Window owner, DatabaseClient client, string database, string table,
        IEnumerable<string> resultColumns, IReadOnlyDictionary<string, object> original)
    {
        var schema = await SchemaAsync(owner, client, database, table);
        if (schema == null) return false;
        if (schema.WhyNotIdentifiable(resultColumns) is { } why) { await Dialogs.ShowMessageAsync(owner, "Edit record", why); return false; }
        var shown = resultColumns.Where(c => schema.Find(c) != null).ToList();
        var form = new RecordEditorWindow($"Edit record in {table}",
            schema.KeyIsExact
                ? "Change the values you want. Only the columns you change are saved."
                : "This table has no primary key, so the record is found by all of its values. If another identical record exists, nothing is changed.",
            schema, shown, original, "Save");
        if (!await form.ShowAsync(owner)) return false;

        var changes = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<KeyValuePair<string, string>>();
        foreach (var pair in form.Values)
        {
            original.TryGetValue(pair.Key, out var before);
            if (RecordValues.SameValue(before, pair.Value)) continue;
            changes[pair.Key] = pair.Value!;
            lines.Add(new KeyValuePair<string, string>(pair.Key, $"{Shorten(RecordValues.ToText(before))}  →  {Shorten(pair.Value ?? "(empty)")}"));
        }
        if (changes.Count == 0)
        {
            await Dialogs.ShowMessageAsync(owner, "Edit record", "Nothing was changed.");
            return false;
        }
        var message = $"Save these changes to the record in {table}?\n\n{Lines(lines)}";
        var (confirmed, _) = await Dialogs.ConfirmWithOptionAsync(owner, "Confirm Change", message, null, "Save");
        if (!confirmed) return false;
        return await RunAsync(owner, "Change", () => client.UpdateRecordAsync(database, schema, original, changes));
    }

    /// <summary>Confirms and deletes the record. Returns true when it was deleted.</summary>
    public static async Task<bool> DeleteAsync(Window owner, DatabaseClient client, string database, string table,
        IEnumerable<string> resultColumns, IReadOnlyDictionary<string, object> original)
    {
        var schema = await SchemaAsync(owner, client, database, table);
        if (schema == null) return false;
        if (schema.WhyNotIdentifiable(resultColumns) is { } why) { await Dialogs.ShowMessageAsync(owner, "Delete record", why); return false; }
        var shown = original.Select(p => new KeyValuePair<string, string>(p.Key, RecordValues.ToText(p.Value)));
        var extra = schema.KeyIsFixed
            ? $"\n\nThis deletes the whole point at that time with those tags, including fields that are not shown."
            : "";
        var message = $"Delete this record from {table}?\n\n{Lines(shown)}{extra}\n\nThis cannot be undone.";
        var (confirmed, _) = await Dialogs.ConfirmWithOptionAsync(owner, "Confirm Delete", message, null, "Delete");
        if (!confirmed) return false;
        return await RunAsync(owner, "Delete", () => client.DeleteRecordAsync(database, schema, original));
    }
}
