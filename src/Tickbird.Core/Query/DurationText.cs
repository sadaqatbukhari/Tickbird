using System.Globalization;
using System.Text.RegularExpressions;

namespace Tickbird.Core.Query;

/// <summary>Reads durations written the way InfluxDB prints them, such as 1h2m3.5s, 250ms or 13µs.</summary>
public static class DurationText
{
    private static readonly Regex Part = new(@"(\d+(?:\.\d+)?)(ns|us|µs|μs|ms|s|m|h)", RegexOptions.Compiled);
    private static readonly Regex Whole = new(@"^\s*(?:\d+(?:\.\d+)?(?:ns|us|µs|μs|ms|s|m|h))+\s*$", RegexOptions.Compiled);

    /// <summary>The duration in milliseconds, or null when the text is not a duration.</summary>
    public static double? ToMilliseconds(string? text)
    {
        if (text == null || !Whole.IsMatch(text)) return null;
        var total = 0.0;
        foreach (Match m in Part.Matches(text))
        {
            var value = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            total += m.Groups[2].Value switch
            {
                "ns" => value / 1_000_000,
                "us" or "µs" or "μs" => value / 1000,
                "ms" => value,
                "s" => value * 1000,
                "m" => value * 60_000,
                _ => value * 3_600_000
            };
        }
        return total;
    }
}
