using Tickbird.Core.Data;
using Tickbird.Core.Providers;
using Tickbird.Core.Completion;
using Tickbird.Core.Navigation;
using Tickbird.Core.Query;
using Tickbird.Core.Settings;

namespace Tickbird.Tests;

public class ParityLogicTests
{
    private static ResultSeries Sample() => new("m",
        new List<string> { "time", "host", "value" },
        new Dictionary<string, string>(),
        new List<IList<object>>
        {
            new List<object> { "2024-01-02T03:04:05Z", "a,b", 1.5 },
            new List<object> { "2024-01-02T03:04:06Z", "say \"hi\"", null! },
        });

    private static readonly ResultFormatter Formatter = new("yyyy-MM-dd", "HH:mm:ss");

    [Fact]
    public void Formatter_FormatsTimeColumns_WithMilliseconds()
    {
        var text = Formatter.Format("time", new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero), System.Globalization.CultureInfo.InvariantCulture)!;
        Assert.Matches(@"^2024-01-0[12] \d\d:\d\d:05\.000$", text);
    }

    [Fact]
    public void Formatter_LeavesOtherColumnsAndNulls()
    {
        Assert.Equal("1.5", Formatter.Format("value", 1.5, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Null(Formatter.Format("value", null));
    }

    [Fact]
    public void Csv_EscapesCommasAndQuotes_AndHonoursRowFilter()
    {
        var writer = new StringWriter();
        ResultExporter.WriteCsv(writer, Sample(), Formatter, new HashSet<int> { 1 });
        var lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("time,host,value", lines[0]);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"say \"\"hi\"\"\"", lines[1]);
    }

    [Fact]
    public void Csv_AllRows_QuotesComma()
    {
        var writer = new StringWriter();
        ResultExporter.WriteCsv(writer, Sample(), Formatter);
        Assert.Contains("\"a,b\"", writer.ToString());
    }

    [Fact]
    public void Json_WritesArrayOfObjects()
    {
        var writer = new StringWriter();
        ResultExporter.WriteJson(writer, Sample());
        var parsed = Newtonsoft.Json.Linq.JArray.Parse(writer.ToString());
        Assert.Equal(2, parsed.Count);
        Assert.Equal("a,b", (string?)parsed[0]["host"]);
    }

    [Fact]
    public void TabSeparated_IncludesHeader_AndEscapesTabs()
    {
        var text = ResultExporter.ToTabSeparated(Sample(), Formatter, new[] { 0 });
        Assert.StartsWith("time\thost\tvalue", text);
        Assert.Equal(2, text.Split('\n').Length);
    }

    [Fact]
    public void SafeFileName_ReplacesInvalidCharacters() =>
        Assert.DoesNotContain(":", ResultExporter.SafeFileName("a:b*c.sql"));

    [Theory]
    [InlineData("select 1", "select 1 from x", "select 1")]
    [InlineData("", "  select 2  ", "select 2")]
    [InlineData(null, null, "")]
    public void TextToExecute_PrefersSelection(string? selected, string? full, string expected) =>
        Assert.Equal(expected, QueryText.TextToExecute(selected, full));

    [Fact]
    public void CommentAndUncomment_RoundTrip()
    {
        var original = "SELECT *\n  FROM m";
        var commented = QueryText.Comment(original);
        Assert.Equal("-- SELECT *\n  -- FROM m", commented);
        Assert.Equal(original, QueryText.Uncomment(commented));
    }

    [Fact]
    public void IsAggregate_DetectsGroupBy()
    {
        Assert.True(QueryText.IsAggregate("select mean(v) from m GROUP BY time(1m)"));
        Assert.False(QueryText.IsAggregate("select 1"));
    }

    [Fact]
    public void SampleQuery_DiffersByServerVersion()
    {
        Assert.Contains("now() - 5m", QueryText.SampleQuery(InfluxDbServerVersion.InfluxDb1, "cpu"));
        Assert.Contains("INTERVAL '5 minutes'", QueryText.SampleQuery(InfluxDbServerVersion.InfluxDb3, "cpu"));
    }

    [Fact]
    public void Commands_ConnectionNode_V1()
    {
        var c = CommandAvailability.For(NodeKind.Connection, "x", InfluxDbServerVersion.InfluxDb1, false, false);
        Assert.True(c.ShowQueries && c.ShowUsers && c.CreateDatabase && c.Refresh && c.Disconnect);
        Assert.False(c.NewQuery || c.TagKeys || c.DropDatabase);
    }

    [Fact]
    public void Commands_ConnectionNode_V3_HidesLegacyOperations()
    {
        var c = CommandAvailability.For(NodeKind.Connection, "x", InfluxDbServerVersion.InfluxDb3, false, false);
        Assert.False(c.ShowQueries || c.ShowPolicies || c.ShowUsers || c.ShowStats || c.ShowDiagnostics);
        Assert.True(c.CreateDatabase);
    }

    [Fact]
    public void Commands_InternalDatabase_CannotBeDropped()
    {
        Assert.False(CommandAvailability.For(NodeKind.Database, "_internal", InfluxDbServerVersion.InfluxDb1, false, false).DropDatabase);
        Assert.True(CommandAvailability.For(NodeKind.Database, "mydb", InfluxDbServerVersion.InfluxDb1, false, false).DropDatabase);
    }

    [Fact]
    public void Commands_Measurement_V3_OnlyTagFieldDrop()
    {
        var c = CommandAvailability.For(NodeKind.Measurement, "cpu", InfluxDbServerVersion.InfluxDb3, true, true);
        Assert.True(c.TagKeys && c.TagValues && c.FieldKeys && c.DropMeasurement && c.NewQuery && c.RunQuery && c.CommentQuery);
        Assert.False(c.ShowSeries || c.DropSeries);
    }

    [Fact]
    public void Commands_NoSelection_DisablesEverythingNodeBased()
    {
        var c = CommandAvailability.For(NodeKind.None, null, (DatabaseProfile?)null, false, false);
        Assert.False(c.Disconnect || c.Refresh || c.NewQuery || c.CreateDatabase);
    }

    [Fact]
    public void Completion_InfluxQl_ContainsKeywordsAndFunctions()
    {
        var items = new CompletionProvider().GetItems(InfluxDbServerVersion.InfluxDb1, "").ToList();
        Assert.Contains(items, i => i.Text.Equals("SELECT", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(items, i => i.Description == "Function");
    }

    [Theory]
    [InlineData("AND")]
    [InlineData("OR")]
    [InlineData("AS")]
    [InlineData("DESC")]
    [InlineData("TZ")]
    [InlineData("SHOW MEASUREMENTS")]
    [InlineData("SHOW TAG VALUES")]
    [InlineData("SHOW RETENTION POLICIES")]
    [InlineData("CREATE DATABASE")]
    [InlineData("DROP MEASUREMENT")]
    [InlineData("NOW()")]
    [InlineData("DERIVATIVE()")]
    [InlineData("NON_NEGATIVE_DERIVATIVE()")]
    [InlineData("MOVING_AVERAGE()")]
    [InlineData("MEDIAN()")]
    [InlineData("TOP()")]
    public void Completion_InfluxQl_OffersTheFullLanguage(string text) =>
        Assert.Contains(new CompletionProvider().GetItems(InfluxDbServerVersion.InfluxDb1, ""), i => i.Text == text);

    [Fact]
    public void Completion_InfluxQl_ListHasNoDuplicates()
    {
        var texts = CompletionProvider.InfluxQlKeywords.Select(k => k.Text).ToList();
        Assert.True(texts.Count > 150);
        Assert.Equal(texts.Count, texts.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Completion_V3_UsesSqlKeywords() =>
        Assert.Contains(new CompletionProvider().GetItems(InfluxDbServerVersion.InfluxDb3, ""), i => i.Text == "DATE_BIN()");

    [Theory]
    [InlineData("SELECT * FROM \"cpu load\" WHERE x", "cpu load")]
    [InlineData("select * from cpu where a", "cpu")]
    [InlineData("SELECT 1", null)]
    public void FindMeasurement(string query, string? expected) =>
        Assert.Equal(expected, CompletionProvider.FindMeasurement(query));

    [Fact]
    public void Settings_Merge_IsIdempotentById()
    {
        var settings = new TickbirdSettings();
        var c = new DatabaseConnection("1", "A", "h", 8086, "u", "p");
        Assert.Equal(1, settings.MergeConnections(new[] { c }));
        Assert.Equal(0, settings.MergeConnections(new[] { new DatabaseConnection("1", "A2", "h", 8086, "u", "p") }));
        Assert.Single(settings.Connections);
    }

    [Fact]
    public void LegacyImporter_ReadsWinFormsFormat_AndToleratesGarbage()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"Version\":\"3.0.7.0\",\"TimeFormat\":\"HH:mm:ss\",\"AllowUntrustedSsl\":true,\"Connections\":[{\"Id\":\"x\",\"Name\":\"Prod\",\"Host\":\"h\",\"Port\":8086}]}");
            var s = LegacySettingsImporter.TryRead(path)!;
            Assert.Equal("HH:mm:ss", s.TimeFormat);
            Assert.True(s.AllowUntrustedSsl);
            Assert.Equal("Prod", Assert.Single(s.Connections).Name);

            File.WriteAllText(path, "nope");
            Assert.Null(LegacySettingsImporter.TryRead(path));
            Assert.Null(LegacySettingsImporter.TryRead(path + ".missing"));
        }
        finally { File.Delete(path); }
    }
}
