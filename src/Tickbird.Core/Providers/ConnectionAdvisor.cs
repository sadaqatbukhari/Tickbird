using Tickbird.Core.Data;

namespace Tickbird.Core.Providers;

public enum AdviceSeverity
{
    /// <summary>Good to know; nothing is wrong.</summary>
    Info,
    /// <summary>Probably a mistake, but the connection can still be saved.</summary>
    Warning,
    /// <summary>The connection cannot be saved like this.</summary>
    Error
}

/// <summary>One note about a connection being edited.</summary>
public sealed record ConnectionAdvice(AdviceSeverity Severity, string Message);

/// <summary>
/// Looks over a connection while it is being typed and says what is probably wrong, before the person presses Test. The
/// rules that depend on the database come from its profile (<see cref="DatabaseProfile.ValidateConnection"/>), so this
/// class has no per-database branches.
/// </summary>
public static class ConnectionAdvisor
{
    /// <summary>The notes for a connection, errors first.</summary>
    public static IReadOnlyList<ConnectionAdvice> Advise(DatabaseConnection connection)
    {
        var profile = connection.Profile;
        var notes = new List<ConnectionAdvice>();

        if (profile.Validate(connection) is { } error) notes.Add(new(AdviceSeverity.Error, error));

        var host = connection.Host ?? "";
        var scheme = SchemeOf(host);
        if (scheme != null) notes.Add(new(AdviceSeverity.Warning, $"Remove \"{scheme}://\". Only the host name is needed."));
        else if (host.Contains('/')) notes.Add(new(AdviceSeverity.Warning, "Remove the path after the host name. Only the host name is needed."));
        else if (host.Any(char.IsWhiteSpace) && host.Trim().Length > 0 && host.Trim().Any(char.IsWhiteSpace))
            notes.Add(new(AdviceSeverity.Warning, "The host name contains a space."));
        else if (HasPort(host)) notes.Add(new(AdviceSeverity.Warning, "Enter the port in the Port box, not after the host name."));

        if (CleanHost(host) is "0.0.0.0") notes.Add(new(AdviceSeverity.Warning, "0.0.0.0 is the address a server listens on. Use localhost or the server's real name."));

        if (profile.Authentication == AuthenticationStyle.Token && string.IsNullOrWhiteSpace(connection.Token))
            notes.Add(new(AdviceSeverity.Warning, $"{profile.DisplayName} needs an API token."));

        if (IsLocal(host) && connection.Port == 5432 && profile.IsSql && profile.Authentication == AuthenticationStyle.UsernamePassword)
            notes.Add(new(AdviceSeverity.Info, "A PostgreSQL installed on this computer may already use port 5432. Change the port if you mapped another."));

        return notes.OrderByDescending(n => n.Severity).ToList();
    }

    /// <summary>The first error, or null: what stops the dialog from saving.</summary>
    public static string? FirstError(DatabaseConnection connection) =>
        Advise(connection).FirstOrDefault(n => n.Severity == AdviceSeverity.Error)?.Message;

    /// <summary>A name for a new connection: <c>crate@localhost</c>, or <c>db.example.com:5433</c> when there is no user.</summary>
    public static string SuggestName(DatabaseConnection connection)
    {
        var profile = connection.Profile;
        var host = CleanHost(connection.Host);
        if (host.Length == 0) return profile.DisplayName;
        var name = profile.Authentication == AuthenticationStyle.UsernamePassword && !string.IsNullOrWhiteSpace(connection.Username)
            ? connection.Username!.Trim() + "@" + host
            : host;
        return connection.Port != 0 && connection.Port != profile.DefaultPort ? $"{name}:{connection.Port}" : name;
    }

    /// <summary>The host name without a scheme, path, port or spaces: what the person meant by <c>http://db.example.com:8086/</c>.</summary>
    public static string CleanHost(string? host)
    {
        var text = (host ?? "").Trim();
        if (SchemeOf(text) is { } scheme) text = text[(scheme.Length + 3)..];
        var cut = text.IndexOfAny(new[] { '/', '?', '#' });
        if (cut >= 0) text = text[..cut];
        var at = text.LastIndexOf('@');
        if (at >= 0) text = text[(at + 1)..];
        if (text.StartsWith('[')) { var end = text.IndexOf(']'); return end > 0 ? text[1..end] : text.TrimStart('['); }
        return HasPort(text) ? text[..text.LastIndexOf(':')] : text;
    }

    /// <summary>The port typed after the host (<c>db:5433</c>), or null.</summary>
    public static ushort? PortInHost(string? host)
    {
        var text = (host ?? "").Trim();
        if (SchemeOf(text) is { } scheme) text = text[(scheme.Length + 3)..];
        var cut = text.IndexOfAny(new[] { '/', '?', '#' });
        if (cut >= 0) text = text[..cut];
        var at = text.LastIndexOf('@');
        if (at >= 0) text = text[(at + 1)..];
        return HasPort(text) && ushort.TryParse(text[(text.LastIndexOf(':') + 1)..], out var port) && port > 0 ? port : null;
    }

    private static bool HasPort(string host)
    {
        var text = host.Trim();
        if (SchemeOf(text) is { } scheme) text = text[(scheme.Length + 3)..];
        var cut = text.IndexOfAny(new[] { '/', '?', '#' });
        if (cut >= 0) text = text[..cut];
        // One colon followed by digits: db:5433. An IPv6 address has several colons or brackets.
        var colon = text.LastIndexOf(':');
        return colon > 0 && text.IndexOf(':') == colon && !text.StartsWith('[') && colon < text.Length - 1 && text[(colon + 1)..].All(char.IsDigit);
    }

    private static string? SchemeOf(string text)
    {
        var colon = text.IndexOf("://", StringComparison.Ordinal);
        return colon > 0 && text[..colon].All(c => char.IsLetterOrDigit(c) || c is '+' or '.' or '-') ? text[..colon].ToLowerInvariant() : null;
    }

    private static bool IsLocal(string? host) =>
        CleanHost(host).ToLowerInvariant() is "localhost" or "127.0.0.1" or "::1";
}
