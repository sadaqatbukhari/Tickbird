using Tickbird.Core.Query;

namespace Tickbird.Tests;

public class DurationTextTests
{
    [Theory]
    [InlineData("1h2m3.5s", 3_723_500)]
    [InlineData("250ms", 250)]
    [InlineData("13µs", 0.013)]
    [InlineData("13us", 0.013)]
    [InlineData("2m", 120_000)]
    [InlineData("1500000ns", 1.5)]
    public void Parses(string text, double ms) => Assert.Equal(ms, DurationText.ToMilliseconds(text)!.Value, 6);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("12")]
    public void NotADuration_IsNull(string? text) => Assert.Null(DurationText.ToMilliseconds(text));
}
