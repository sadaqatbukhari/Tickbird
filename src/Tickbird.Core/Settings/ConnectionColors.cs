namespace Tickbird.Core.Settings;

/// <summary>The colours a connection or a group can be given, to tell servers apart at a glance (for example red for PROD).</summary>
public static class ConnectionColors
{
    /// <summary>Name and #RRGGBB value of each colour on offer.</summary>
    public static readonly IReadOnlyList<(string Name, string Hex)> Palette = new[]
    {
        ("Red", "#D64545"), ("Orange", "#E8833A"), ("Amber", "#D9A520"), ("Green", "#3E9B5F"),
        ("Teal", "#1F9AA8"), ("Blue", "#2F6FD0"), ("Purple", "#7A55C4"), ("Pink", "#C9508B"), ("Grey", "#6B778C")
    };

    /// <summary>Returns the colour as upper-case #RRGGBB, or null when blank or not a valid colour (settings can be edited by hand).</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.Length != 7 || text[0] != '#' || !text.Skip(1).All(Uri.IsHexDigit)) return null;
        return text.ToUpperInvariant();
    }
}
