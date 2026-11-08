using Tickbird.Core.Data;
using Tickbird.Core.Navigation;
using Tickbird.Core.Query;

namespace Tickbird.Tests;

public class RulesTests
{
    private static RetentionPolicy Policy(string name, bool isDefault = false) =>
        new() { Name = name, Duration = "1d", ReplicationCopies = 1, Default = isDefault };

    [Theory]
    [InlineData("", "1d", "name cannot be blank")]
    [InlineData("rp", "", "Duration cannot be blank")]
    [InlineData("rp", "banana", "Duration value is invalid")]
    [InlineData("rp", "30d", null)]
    public void RetentionPolicyValidation(string name, string duration, string? expectedFragment)
    {
        var error = Validation.RetentionPolicy(name, duration);
        if (expectedFragment == null) Assert.Null(error);
        else Assert.Contains(expectedFragment, error);
    }

    [Fact]
    public void UserValidation_RequiresNameAndPassword()
    {
        Assert.NotNull(Validation.User("", "x"));
        Assert.NotNull(Validation.User("bob", " "));
        Assert.Null(Validation.User("bob", "pw"));
    }

    [Fact]
    public void DatabaseValidation_RejectsBlankAndDuplicate()
    {
        Assert.NotNull(Validation.Database(" ", null));
        Assert.Contains("already exists", Validation.Database("a", new[] { "a", "b" }));
        Assert.Null(Validation.Database("c", new[] { "a", "b" }));
    }

    [Fact]
    public void ContinuousQueryValidation_ChecksEachField()
    {
        const string ok = "SELECT mean(v) INTO x FROM y";
        Assert.Contains("name", Validation.ContinuousQuery("", "d", "s", "1m", false, null, null, ok));
        Assert.Contains("Destination", Validation.ContinuousQuery("n", "", "s", "1m", false, null, null, ok));
        Assert.Contains("Source", Validation.ContinuousQuery("n", "d", "", "1m", false, null, null, ok));
        Assert.Contains("Interval", Validation.ContinuousQuery("n", "d", "s", "zz", false, null, null, ok));
        Assert.Contains("Resample Every", Validation.ContinuousQuery("n", "d", "s", "1m", true, "zz", null, ok));
        Assert.Contains("Resample For", Validation.ContinuousQuery("n", "d", "s", "1m", true, "1m", "zz", ok));
        Assert.Contains("SubQuery", Validation.ContinuousQuery("n", "d", "s", "1m", false, null, null, Validation.SubQueryPlaceholder));
        Assert.Null(Validation.ContinuousQuery("n", "d", "s", "1m", true, "5m", "1h", ok));
    }

    [Fact]
    public void ContinuousQueryValidation_IgnoresResampleValuesWhenDisabled() =>
        Assert.Null(Validation.ContinuousQuery("n", "d", "s", "1m", false, "garbage", "garbage", "SELECT 1"));

    [Fact]
    public void BackfillValidation_RequiresOrderedTimes()
    {
        var now = DateTime.Now;
        Assert.Contains("later than", Validation.Backfill("d", "s", "1m", now, now.AddHours(-1), "SELECT 1"));
        Assert.Null(Validation.Backfill("d", "s", "1m", now.AddHours(-1), now, "SELECT 1"));
    }

    [Fact]
    public void SplitList_TrimsAndReturnsNullWhenEmpty()
    {
        Assert.Null(Validation.SplitList("  "));
        Assert.Null(Validation.SplitList(" , ,"));
        Assert.Equal(new[] { "a", "b" }, Validation.SplitList(" a , b ,"));
    }

    [Fact]
    public void FallbackDefault_PrefersAutogen_ThenAnyOther()
    {
        var policies = new[] { Policy("weekly", true), Policy("autogen"), Policy("monthly") };
        Assert.Equal("autogen", RetentionPolicyRules.ChooseFallbackDefault(policies, "weekly")!.Name);
        Assert.Equal("monthly", RetentionPolicyRules.ChooseFallbackDefault(new[] { Policy("weekly"), Policy("monthly") }, "weekly")!.Name);
        Assert.Null(RetentionPolicyRules.ChooseFallbackDefault(new[] { Policy("only") }, "only"));
    }

    [Fact]
    public void FieldsPolicyOrdering_PutsDefaultFirst()
    {
        var ordered = RetentionPolicyRules.OrderForFields(new[] { Policy("a"), Policy("b", true), Policy("c") });
        Assert.Equal("b", ordered[0].Name);
    }

    [Fact]
    public void SeriesTable_BuildsOneColumnPerTagKey()
    {
        var table = SeriesTable.Parse("cpu", new[] { "cpu,host=a,region=eu", "cpu,host=b", "cpu" });
        Assert.Equal(new[] { "host", "region" }, table.Columns);
        Assert.Equal(3, table.Rows.Count);
        Assert.Equal(new[] { "a", "eu" }, table.Rows[0]);
        Assert.Equal(new[] { "b", null }, table.Rows[1]);
        Assert.Equal(new string?[] { null, null }, table.Rows[2]);
    }

    [Fact]
    public void TableExport_Csv_AndJson_RespectSelection()
    {
        var table = new TableData(new[] { "tagKey" }, new List<IReadOnlyList<string?>> { new[] { "a,b" }, new[] { "c" } });
        var csv = new StringWriter();
        ResultExporter.WriteCsv(csv, table, new HashSet<int> { 0 });
        Assert.Contains("\"a,b\"", csv.ToString());
        Assert.DoesNotContain("c" + Environment.NewLine, csv.ToString().Replace("tagKey", ""));
        var json = new StringWriter();
        ResultExporter.WriteJson(json, table);
        Assert.Equal(2, Newtonsoft.Json.Linq.JArray.Parse(json.ToString()).Count);
    }
}
