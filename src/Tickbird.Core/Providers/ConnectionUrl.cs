using System.Globalization;

namespace Tickbird.Core.Providers;

/// <summary>What a pasted connection string says. A null part was not in the string.</summary>
public sealed record ParsedConnectionUrl(DatabaseKind Kind, string Host, ushort Port, string? Username, string? Password,
    string? Database, bool UseSsl, string? Token);

/// <summary>
/// Reads connection strings such as <c>postgresql://app:pw@db.example.com:5432/metrics?sslmode=require</c>,
/// <c>crate://crate@localhost:5432</c> or <c>https://influx.example.com:8086</c>. Which database a scheme means comes from
/// the profiles (<see cref="DatabaseProfile.UrlSchemes"/>), so a new database adds its own scheme without a change here.
/// </summary>
public static class ConnectionUrl
{
    private static readonly HashSet<string> SecureModes = new(StringComparer.OrdinalIgnoreCase) { "require", "verify-ca", "verify-full" };
    private static readonly HashSet<string> TrueWords = new(StringComparer.OrdinalIgnoreCase) { "true", "1", "yes", "on", "require" };

    /// <summary>True when the text looks like a connection string (it starts with a scheme and <c>://</c>).</summary>
    public static bool LooksLikeUrl(string? text) => SchemeOf(text) != null;

    private static string? SchemeOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        var colon = trimmed.IndexOf("://", StringComparison.Ordinal);
        if (colon <= 0) return null;
        var scheme = trimmed[..colon];
        return scheme.All(c => char.IsLetterOrDigit(c) || c is '+' or '.' or '-') && char.IsLetter(scheme[0]) ? scheme.ToLowerInvariant() : null;
    }

    /// <summary>Reads the text, or returns null when it is not a connection string of a database Tickbird knows.</summary>
    public static ParsedConnectionUrl? TryParse(string? text)
    {
        var scheme = SchemeOf(text);
        if (scheme == null || !Uri.TryCreate(text!.Trim(), UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return null;

        var claimed = DatabaseProfiles.All.Where(p => p.UrlSchemes.Contains(scheme, StringComparer.OrdinalIgnoreCase)).ToList();
        if (claimed.Count == 0) return null;
        // Uri reports 80 and 443 for http and https when no port was typed; that is not a port the person chose.
        var explicitPort = uri.Port > 0 && !(scheme is "http" or "https" && uri.IsDefaultPort);
        // http and https belong to more than one database; the port tells which (8181 is InfluxDB 3.x).
        var profile = (explicitPort ? claimed.FirstOrDefault(p => p.DefaultPort == uri.Port) : null) ?? claimed[0];
        var port = explicitPort ? uri.Port : profile.DefaultPort;
        if (port is < 1 or > 65535) return null;

        string? user = null, password = null;
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var colon = uri.UserInfo.IndexOf(':');
            user = Unescape(colon < 0 ? uri.UserInfo : uri.UserInfo[..colon]);
            password = colon < 0 ? null : Unescape(uri.UserInfo[(colon + 1)..]);
        }

        var query = ParseQuery(uri.Query);
        var database = uri.AbsolutePath.Trim('/');
        if (database.Length == 0 && query.TryGetValue("database", out var d)) database = d;
        if (database.Length == 0 && query.TryGetValue("db", out var d2)) database = d2;

        var secure = scheme == "https"
            || (query.TryGetValue("sslmode", out var mode) && SecureModes.Contains(mode))
            || (query.TryGetValue("ssl", out var ssl) && TrueWords.Contains(ssl))
            || (query.TryGetValue("tls", out var tls) && TrueWords.Contains(tls));

        query.TryGetValue("token", out var token);
        var tokenLogin = profile.Authentication == AuthenticationStyle.Token;
        return new ParsedConnectionUrl(profile.Kind, uri.Host.Trim('[', ']'), (ushort)port,
            tokenLogin ? null : user, tokenLogin ? null : password,
            database.Length == 0 ? null : Unescape(database), secure, tokenLogin ? token ?? password : null);
    }

    private static string Unescape(string text) => Uri.UnescapeDataString(text);

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=');
            var key = Uri.UnescapeDataString(equals < 0 ? part : part[..equals]);
            result[key] = equals < 0 ? "" : Uri.UnescapeDataString(part[(equals + 1)..].Replace('+', ' '));
        }
        return result;
    }
}
