using Tickbird.Core.Completion;
using Tickbird.Core.Data;

namespace Tickbird.Tests;

public class TagValueCompletionTests
{
    private static CompletionProvider Provider()
    {
        var p = new CompletionProvider();
        p.SetSchema(new[]
        {
            new MeasurementSchema("cpu", new[] { new FieldKey("usage", "float") }, new[] { "host", "region" }),
            new MeasurementSchema("mem", new[] { new FieldKey("used", "float") }, new[] { "host" }),
        });
        return p;
    }

    [Theory]
    [InlineData("SELECT * FROM cpu WHERE host = ", "host", 31, "")]
    [InlineData("SELECT * FROM cpu WHERE host=", "host", 29, "")]
    [InlineData("SELECT * FROM cpu WHERE host != 'se", "host", 32, "se")]
    [InlineData("SELECT * FROM cpu WHERE host <> '", "host", 32, "")]
    [InlineData("WHERE \"my tag\" = 'a b", "my tag", 17, "a b")]
    [InlineData("WHERE host = 'a' AND region = ", "region", 30, "")]
    public void FindTagValueContext_Matches(string text, string tag, int start, string prefix)
    {
        var context = CompletionProvider.FindTagValueContext(text, text);
        Assert.NotNull(context);
        Assert.Equal(tag, context!.Tag);
        Assert.Equal(start, context.Start);
        Assert.Equal(prefix, context.Prefix);
    }

    [Theory]
    [InlineData("WHERE host = 'a'")]       // value already closed
    [InlineData("WHERE host =~ /a")]       // regex match, not a value list
    [InlineData("-- WHERE host = ")]       // comment
    [InlineData("SELECT host ")]
    public void FindTagValueContext_NoMatch(string text) =>
        Assert.Null(CompletionProvider.FindTagValueContext(text, text));

    [Fact]
    public void FindTag_MatchesKnownTagsOnly_IgnoringCase()
    {
        var p = Provider();
        Assert.Equal("host", p.FindTag("HOST"));
        Assert.Null(p.FindTag("usage"));   // a field, not a tag
        Assert.Null(p.FindTag("nope"));
    }

    [Fact]
    public void TagValueSources_UseFromMeasurement_ElseAllMeasurementsWithTheTag()
    {
        var p = Provider();
        Assert.Equal(new[] { "cpu" }, p.TagValueSources("CPU", "host"));
        Assert.Equal(new[] { "cpu", "mem" }, p.TagValueSources(null, "host"));
        Assert.Equal(new[] { "cpu" }, p.TagValueSources("mem", "region"));   // FROM measurement lacks the tag
    }

    [Fact]
    public void TagValueItems_AreKeptWithTheTag_QuotedSortedAndDistinct()
    {
        var p = Provider();
        Assert.False(p.HasTagValues("cpu", "host"));
        p.SetTagValues("cpu", "host", new[] { "web-2", "db-1", "web-2" });
        p.SetTagValues("mem", "host", new[] { "cache-1", "db-1" });
        Assert.True(p.HasTagValues("cpu", "host"));

        var items = p.GetTagValueItems(InfluxDbServerVersion.InfluxDb1, new[] { "cpu", "mem" }, "host");
        Assert.Equal(new[] { "'cache-1'", "'db-1'", "'web-2'" }, items.Select(i => i.Text));
        Assert.All(items, i => Assert.Equal(CompletionKind.TagValue, i.Kind));
        Assert.Equal("Value of tag host", items[0].Description);
        Assert.Equal(new[] { "'db-1'", "'web-2'" }, p.GetTagValueItems(InfluxDbServerVersion.InfluxDb1, new[] { "cpu" }, "host").Select(i => i.Text));
    }

    [Fact]
    public void TagValueItems_AreCapped()
    {
        var p = Provider();
        p.SetTagValues("cpu", "host", Enumerable.Range(0, 5000).Select(i => "h" + i));
        Assert.Equal(CompletionProvider.MaxTagValues, p.GetTagValueItems((InfluxDbServerVersion?)null, new[] { "cpu" }, "host").Count);
    }

    [Fact]
    public void QuoteValue_EscapesPerLanguage()
    {
        Assert.Equal("'o\\'neil'", CompletionProvider.QuoteValue("o'neil", InfluxDbServerVersion.InfluxDb1));
        Assert.Equal("'a\\\\b'", CompletionProvider.QuoteValue("a\\b", InfluxDbServerVersion.InfluxDb1));
        Assert.Equal("'o''neil'", CompletionProvider.QuoteValue("o'neil", InfluxDbServerVersion.InfluxDb3));
    }

    [Theory]
    [InlineData("SELECT * FROM cpu WHERE ", true)]
    [InlineData("SELECT * FROM cpu WHERE a = 'b' AND ho", true)]
    [InlineData("SELECT ho", false)]
    [InlineData("SELECT * FROM cpu GROUP BY ", false)]
    public void IsFilterContext(string text, bool expected) =>
        Assert.Equal(expected, CompletionProvider.IsFilterContext(text));

    [Fact]
    public async Task SchemaCache_KeepsTagValues_UntilInvalidated()
    {
        var cache = new SchemaCache();
        var calls = 0;
        Task<IReadOnlyList<string>> Load() { calls++; return Task.FromResult<IReadOnlyList<string>>(new[] { "a" }); }
        await cache.GetTagValuesAsync("c", "db", "cpu", "host", Load);
        await cache.GetTagValuesAsync("c", "db", "cpu", "host", Load);
        Assert.Equal(1, calls);
        await cache.GetTagValuesAsync("c", "db", "cpu", "region", Load);
        Assert.Equal(2, calls);
        cache.Invalidate("c", "db");
        await cache.GetTagValuesAsync("c", "db", "cpu", "host", Load);
        Assert.Equal(3, calls);
    }
}
