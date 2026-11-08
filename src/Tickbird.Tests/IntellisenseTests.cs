using Tickbird.Core.Completion;
using Tickbird.Core.Data;

namespace Tickbird.Tests;

public class IntellisenseTests
{
    private static ResultSeries Series(string name, string[] columns, params object[][] rows) =>
        new(name, columns.ToList(), new Dictionary<string, string>(), rows.Select(r => (IList<object>)r.ToList()).ToList());

    private static IReadOnlyList<MeasurementSchema> SampleSchema() => new[]
    {
        new MeasurementSchema("cpu", new[] { new FieldKey("usage_idle", "float"), new FieldKey("usage_user", "float") }, new[] { "host", "cpu" }),
        new MeasurementSchema("mem", new[] { new FieldKey("used_percent", "float") }, new[] { "host" }),
        new MeasurementSchema("disk io", new[] { new FieldKey("reads", "integer") }, Array.Empty<string>()),
    };

    [Fact]
    public void FromInfluxQl_MergesFieldsAcrossPolicies_AndAddsTags()
    {
        var fields = new[]
        {
            Series("cpu", new[] { "fieldKey", "fieldType" }, new object[] { "usage_idle", "float" }),
            Series("cpu", new[] { "fieldKey", "fieldType" }, new object[] { "usage_idle", "float" }, new object[] { "usage_user", "float" }),
            Series("mem", new[] { "fieldKey", "fieldType" }, new object[] { "used", "integer" }),
        };
        var tags = new[] { Series("cpu", new[] { "tagKey" }, new object[] { "host" }, new object[] { "cpu" }) };
        var schema = MeasurementSchema.FromInfluxQl(fields, tags, new[] { "cpu", "mem", "empty" });

        Assert.Equal(new[] { "cpu", "empty", "mem" }, schema.Select(s => s.Name));
        var cpu = schema[0];
        Assert.Equal(new[] { "usage_idle", "usage_user" }, cpu.Fields.Select(f => f.Name));
        Assert.Equal(new[] { "host", "cpu" }, cpu.Tags);
        Assert.Empty(schema[1].Fields);
    }

    [Fact]
    public void FromInformationSchema_SplitsTagsAndFields_AndSkipsTime()
    {
        var series = new[]
        {
            Series("", new[] { "table_name", "column_name", "data_type" },
                new object[] { "cpu", "host", "Dictionary(Int32, Utf8)" },
                new object[] { "cpu", "time", "Timestamp(Nanosecond, None)" },
                new object[] { "cpu", "usage", "Float64" },
                new object[] { "mem", "free", "Int64" }),
        };
        var schema = MeasurementSchema.FromInformationSchema(series);
        Assert.Equal(new[] { "cpu", "mem" }, schema.Select(s => s.Name));
        Assert.Equal(new[] { "host" }, schema[0].Tags);
        Assert.Equal(new[] { "usage" }, schema[0].Fields.Select(f => f.Name));
        Assert.Equal("Float64", schema[0].Fields[0].Type);
    }

    [Theory]
    [InlineData("SELECT ", CompletionContext.Column)]
    [InlineData("SELECT mean(usa", CompletionContext.Column)]
    [InlineData("SELECT * FROM ", CompletionContext.Measurement)]
    [InlineData("SELECT * FROM c", CompletionContext.Measurement)]
    [InlineData("SELECT * FROM \"disk", CompletionContext.Measurement)]
    [InlineData("SELECT * FROM cpu WHERE ", CompletionContext.Column)]
    [InlineData("SELECT * FROM cpu WHERE host = 'a' AND ", CompletionContext.Column)]
    [InlineData("SELECT * FROM cpu GROUP BY ", CompletionContext.Column)]
    [InlineData("SELECT * FROM cpu LIMIT ", CompletionContext.Any)]
    [InlineData("", CompletionContext.Any)]
    public void DetectContext(string before, CompletionContext expected) =>
        Assert.Equal(expected, CompletionProvider.DetectContext(before));

    [Fact]
    public void AfterFrom_MeasurementsComeFirst()
    {
        var provider = new CompletionProvider();
        provider.SetSchema(SampleSchema());
        var items = provider.GetItems(InfluxDbServerVersion.InfluxDb1, "SELECT * FROM ", "SELECT * FROM ").ToList();
        Assert.Equal(new[] { "cpu", "disk io", "mem" }, items.Take(3).Select(i => i.Text));
        Assert.All(items.Take(3), i => Assert.Equal(CompletionKind.Measurement, i.Kind));
    }

    [Fact]
    public void InWhere_FieldsAndTagsOfFromMeasurementComeFirst_ThenOtherMeasurements()
    {
        var provider = new CompletionProvider();
        provider.SetSchema(SampleSchema());
        const string query = "SELECT * FROM mem WHERE ";
        var items = provider.GetItems(InfluxDbServerVersion.InfluxDb1, query, query).ToList();
        Assert.Equal(new[] { "used_percent", "host" }, items.Take(2).Select(i => i.Text));
        Assert.Contains(items, i => i.Text == "usage_idle" && i.Kind == CompletionKind.Field);
        Assert.Contains("float", items[0].Description);
    }

    [Fact]
    public void WithoutFrom_AllFieldsOfTheDatabaseAreOffered()
    {
        var provider = new CompletionProvider();
        provider.SetSchema(SampleSchema());
        var items = provider.GetItems(InfluxDbServerVersion.InfluxDb1, "SELECT ", "SELECT ").Select(i => i.Text).ToList();
        Assert.Contains("usage_idle", items);
        Assert.Contains("used_percent", items);
        Assert.Contains("reads", items);
        Assert.Equal(4, provider.FieldCount);
        Assert.Equal(3, provider.TagCount);
    }

    [Fact]
    public void Identifiers_AreQuotedWhenNeeded()
    {
        Assert.Equal("cpu", CompletionProvider.Quote("cpu", InfluxDbServerVersion.InfluxDb1));
        Assert.Equal("\"disk io\"", CompletionProvider.Quote("disk io", InfluxDbServerVersion.InfluxDb1));
        Assert.Equal("\"a\\\"b\"", CompletionProvider.Quote("a\"b", InfluxDbServerVersion.InfluxDb1));
        Assert.Equal("\"select\"", CompletionProvider.Quote("select", InfluxDbServerVersion.InfluxDb1));
        // SQL folds unquoted names to lower case, so mixed-case names must be quoted for 3.x.
        Assert.Equal("\"Temperature\"", CompletionProvider.Quote("Temperature", InfluxDbServerVersion.InfluxDb3));
        Assert.Equal("temperature", CompletionProvider.Quote("temperature", InfluxDbServerVersion.InfluxDb3));

        var provider = new CompletionProvider();
        provider.SetSchema(SampleSchema());
        var disk = provider.GetItems(InfluxDbServerVersion.InfluxDb1, "", "SELECT * FROM ").Single(i => i.Text == "disk io");
        Assert.Equal("\"disk io\"", disk.InsertText);
    }

    [Theory]
    [InlineData("SELECT u", "SELECT u", "u", true)]          // new word starts
    [InlineData("SELECT us", "SELECT us", "s", true)]        // reopened mid-word if the list was closed
    [InlineData("SELECT ", "SELECT ", " ", true)]            // after a keyword
    [InlineData("SELECT * FROM ", "SELECT * FROM ", " ", true)]
    [InlineData("SELECT x ", "SELECT x ", " ", false)]
    [InlineData("SELECT a,", "SELECT a,", ",", true)]
    [InlineData("SELECT 5m", "SELECT 5m", "m", false)]       // numbers/durations
    [InlineData("-- n", "-- n", "n", false)]                 // comment
    [InlineData("WHERE host = 'a", "WHERE host = 'a", "a", false)]   // string literal
    [InlineData("FROM \"d", "FROM \"d", "d", true)]           // quoted identifier
    public void ShouldOpen(string before, string line, string typed, bool expected) =>
        Assert.Equal(expected, CompletionTrigger.ShouldOpen(before, line, typed));

    [Theory]
    [InlineData("SELECT usa", 10, 7)]
    [InlineData("FROM \"disk", 10, 5)]
    [InlineData("SELECT ", 7, 7)]
    public void WordStart(string text, int caret, int expected) =>
        Assert.Equal(expected, CompletionTrigger.WordStart(text, caret));

    [Fact]
    public async Task SchemaCache_LoadsOnce_RetriesAfterFailure_AndInvalidates()
    {
        var cache = new SchemaCache();
        var calls = 0;
        Task<IReadOnlyList<MeasurementSchema>> Load() { calls++; return Task.FromResult(SampleSchema()); }

        var first = cache.GetAsync("c1", "db", Load);
        var second = cache.GetAsync("c1", "db", Load);
        Assert.Same(first, second);
        await first;
        Assert.Equal(1, calls);
        Assert.NotNull(cache.TryGetLoaded("c1", "db"));
        Assert.Null(cache.TryGetLoaded("c1", "other"));

        cache.Invalidate("c1", "db");
        await cache.GetAsync("c1", "db", Load);
        Assert.Equal(2, calls);

        cache.Invalidate("c1");
        Assert.Null(cache.TryGetLoaded("c1", "db"));

        var failing = cache.GetAsync("c2", "db", () => Task.FromException<IReadOnlyList<MeasurementSchema>>(new InvalidOperationException("no access")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing);
        var retried = await cache.GetAsync("c2", "db", Load);
        Assert.Equal(3, retried.Count);
    }
}
