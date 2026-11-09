using Tickbird.Core.Data;
using Tickbird.Core.Providers;

namespace Tickbird.Tests;

/// <summary>Inserting, changing and deleting one record: which queries can be edited, the SQL written, and the safety checks.</summary>
public class RecordEditingTests
{
    [Theory]
    [InlineData("SELECT * FROM cpu", "cpu")]
    [InlineData("select * from \"cpu\" where host = 'a' order by time desc limit 10;", "\"cpu\"")]
    [InlineData("SELECT time, host, usage FROM public.cpu WHERE usage > 3", "public.cpu")]
    [InlineData("SELECT * FROM \"my table\" LIMIT 5", "\"my table\"")]
    [InlineData("-- a comment\nSELECT * FROM cpu /* x */ LIMIT 1", "cpu")]
    [InlineData("SELECT * FROM \"cpu\" WHERE time > now() - 5m LIMIT 100", "\"cpu\"")]
    [InlineData("SELECT * FROM cpu WHERE host IN ('a', 'b') ORDER BY host, time", "cpu")]
    public void PlainSelects_NameTheirTable(string query, string table) => Assert.Equal(table, RecordQuery.FindTable(query));

    [Theory]
    [InlineData("SELECT count(*) FROM cpu")]
    [InlineData("SELECT mean(usage) FROM cpu GROUP BY time(1m)")]
    [InlineData("SELECT host, avg(usage) FROM cpu GROUP BY host")]
    [InlineData("SELECT * FROM a JOIN b ON a.id = b.id")]
    [InlineData("SELECT * FROM a, b")]
    [InlineData("SELECT * FROM (SELECT * FROM cpu) t")]
    [InlineData("SELECT * FROM cpu; SELECT * FROM mem")]
    [InlineData("SELECT DISTINCT host FROM cpu")]
    [InlineData("SELECT * FROM cpu UNION SELECT * FROM mem")]
    [InlineData("DELETE FROM cpu")]
    [InlineData("SHOW MEASUREMENTS")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherQueries_AreNotEditable(string? query) => Assert.Null(RecordQuery.FindTable(query));

    [Fact]
    public void NameParts_AreSplitWithoutQuotes()
    {
        Assert.Equal(new[] { "public", "my.table" }, RecordQuery.Parts("public.\"my.table\""));
        Assert.Equal(new[] { "a\"b" }, RecordQuery.Parts("\"a\"\"b\""));
    }

    private static RecordSchema Keyed() => new("cpu", new[]
    {
        new RecordColumn("id", "integer", IsKey: true), new RecordColumn("host", "text"), new RecordColumn("usage", "double precision")
    }, keyIsExact: true);

    private static RecordSchema Keyless() => new("cpu", new[]
    {
        new RecordColumn("time", "timestamp with time zone", IsTime: true), new RecordColumn("host", "text"), new RecordColumn("usage", "double precision")
    }, keyIsExact: false);

    [Fact]
    public void ARecord_CanBeFound_OnlyWhenTheResultHoldsItsIdentity()
    {
        Assert.Null(Keyed().WhyNotIdentifiable(new[] { "ID", "host" }));
        Assert.Contains("key column(s) id", Keyed().WhyNotIdentifiable(new[] { "host", "usage" }));
        Assert.Null(Keyless().WhyNotIdentifiable(new[] { "time", "host", "usage" }));
        Assert.Contains("no primary key", Keyless().WhyNotIdentifiable(new[] { "time", "host" }));
    }

    private static (TimescaleDbClient Client, FakeSqlRunner Runner) Create(Action<FakeSqlRunner>? answers = null)
    {
        var runner = new FakeSqlRunner();
        answers?.Invoke(runner);
        var connection = new DatabaseConnection("1", "ts", "localhost", 5432, "app", "secret") { Kind = DatabaseKind.TimescaleDb };
        return (new TimescaleDbClient(connection, runner), runner);
    }

    [Fact]
    public async Task TheSchema_UsesThePrimaryKey_WhenThereIsOne()
    {
        var (client, _) = Create(r => r
            .Answer("i.indisprimary", new[] { "attname" }, new object[] { "id" })
            .Answer("a.attname AS column_name", new[] { "column_name", "data_type" },
                new object[] { "id", "integer" }, new object[] { "host", "text" }, new object[] { "time", "timestamp with time zone" }));
        var schema = await client.GetRecordSchemaAsync("metrics", "cpu");
        Assert.True(schema.KeyIsExact);
        Assert.Equal(new[] { "id" }, schema.Identity.Select(c => c.Name));
        Assert.True(schema.Find("time")!.IsTime);
    }

    [Fact]
    public async Task TheSchema_FallsBackToAllColumns_WithoutAPrimaryKey()
    {
        var (client, _) = Create(r => r.Answer("a.attname AS column_name", new[] { "column_name", "data_type" },
            new object[] { "time", "timestamptz" }, new object[] { "host", "text" }));
        var schema = await client.GetRecordSchemaAsync("metrics", "cpu");
        Assert.False(schema.KeyIsExact);
        Assert.Equal(2, schema.Identity.Count());
    }

    [Fact]
    public void Update_SetsTheChangedColumns_AndFindsTheRecordByItsKey()
    {
        var (client, _) = Create();
        var (sql, parameters) = client.BuildRecordUpdate(Keyed(),
            new Dictionary<string, object> { ["id"] = 7L, ["host"] = "a", ["usage"] = 1.5 },
            new Dictionary<string, object> { ["host"] = "b", ["usage"] = null! });
        Assert.Equal("UPDATE \"public\".\"cpu\" SET \"host\" = @s0, \"usage\" = @s1 WHERE \"id\" = @k0", sql);
        Assert.Equal(7L, parameters["k0"]);
        Assert.Null(parameters["s1"]);
    }

    [Fact]
    public void NullIdentityValues_AreMatchedWithIsNull()
    {
        var (client, _) = Create();
        var (sql, parameters) = client.BuildRecordDelete(Keyless(), new Dictionary<string, object>
        {
            ["time"] = new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc), ["host"] = null!, ["usage"] = 2.5
        });
        Assert.Equal("DELETE FROM \"public\".\"cpu\" WHERE \"time\" = @k0 AND \"host\" IS NULL AND \"usage\" = @k1", sql);
        Assert.Equal(2, parameters.Count);
    }

    [Fact]
    public void Insert_NamesOnlyTheColumnsGiven()
    {
        var (client, _) = Create();
        var (sql, parameters) = client.BuildRecordInsert(Keyed(), new Dictionary<string, object> { ["id"] = "1", ["host"] = "web-1" });
        Assert.Equal("INSERT INTO \"public\".\"cpu\" (\"id\", \"host\") VALUES (@v0, @v1)", sql);
        Assert.Equal(2, parameters.Count);
        Assert.Throws<InvalidOperationException>(() => client.BuildRecordInsert(Keyed(), new Dictionary<string, object>()));
    }

    [Fact]
    public async Task Delete_GoesAhead_OnlyWhenExactlyOneRecordMatches()
    {
        var (one, oneRunner) = Create(r => r.Answer("SELECT count(*)", new[] { "count" }, new object[] { 1L }));
        var ok = await one.DeleteRecordAsync("metrics", Keyed(), new Dictionary<string, object> { ["id"] = 1L });
        Assert.True(ok.Success);
        Assert.Single(oneRunner.Executed);
        Assert.StartsWith("DELETE FROM", oneRunner.Executed.Single().Sql);

        var (none, noneRunner) = Create(r => r.Answer("SELECT count(*)", new[] { "count" }, new object[] { 0L }));
        var missing = await none.DeleteRecordAsync("metrics", Keyed(), new Dictionary<string, object> { ["id"] = 1L });
        Assert.False(missing.Success);
        Assert.Contains("not found", missing.Body);
        Assert.Empty(noneRunner.Executed);

        var (many, manyRunner) = Create(r => r.Answer("SELECT count(*)", new[] { "count" }, new object[] { 3L }));
        var ambiguous = await many.DeleteRecordAsync("metrics", Keyless(), new Dictionary<string, object>
            { ["time"] = DateTime.UtcNow, ["host"] = "a", ["usage"] = 1.0 });
        Assert.False(ambiguous.Success);
        Assert.Contains("3 identical records", ambiguous.Body);
        Assert.Empty(manyRunner.Executed);
    }

    [Fact]
    public async Task Update_IsRefused_WhenTheRecordIsNotTheOnlyMatch()
    {
        var (client, runner) = Create(r => r.Answer("SELECT count(*)", new[] { "count" }, new object[] { 2L }));
        var result = await client.UpdateRecordAsync("metrics", Keyless(),
            new Dictionary<string, object> { ["time"] = DateTime.UtcNow, ["host"] = "a", ["usage"] = 1.0 },
            new Dictionary<string, object> { ["usage"] = "2" });
        Assert.False(result.Success);
        Assert.Empty(runner.Executed);
    }

    [Fact]
    public void WhoCanDoWhat_FollowsTheDatabase()
    {
        DatabaseCapabilities Caps(DatabaseKind k) => DatabaseProfiles.Get(k).Capabilities & DatabaseCapabilities.AllRecordOperations;
        Assert.Equal(DatabaseCapabilities.AllRecordOperations, Caps(DatabaseKind.InfluxDb1));
        Assert.Equal(DatabaseCapabilities.AllRecordOperations, Caps(DatabaseKind.TimescaleDb));
        Assert.Equal(DatabaseCapabilities.AllRecordOperations, Caps(DatabaseKind.CrateDb));
        Assert.Equal(DatabaseCapabilities.EditRecords | DatabaseCapabilities.InsertRecords, Caps(DatabaseKind.InfluxDb3));
    }

    // ------------------------------------------------------------------ InfluxDB points

    private static RecordSchema Points() => new("cpu", new[]
    {
        new RecordColumn("time", "time", IsKey: true, IsTime: true), new RecordColumn("host", "tag", IsKey: true),
        new RecordColumn("usage", "float"), new RecordColumn("cores", "integer"), new RecordColumn("note", "string")
    }, keyIsExact: true, keyIsFixed: true);

    [Fact]
    public void ADelete_FindsAPointByItsTimeAndTags()
    {
        var original = new Dictionary<string, object>
        {
            ["time"] = new DateTime(2024, 5, 1, 12, 30, 0, 123, DateTimeKind.Utc), ["host"] = "web'1", ["usage"] = 1.5
        };
        Assert.Equal("DELETE FROM \"cpu\" WHERE time = '2024-05-01T12:30:00.123Z' AND \"host\" = 'web\\'1'",
            LineProtocolRecords.DeleteStatement(Points(), original));
    }

    [Fact]
    public void APointWithoutItsTags_CannotBeFound()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            LineProtocolRecords.DeleteStatement(Points(), new Dictionary<string, object> { ["time"] = DateTime.UtcNow }));
        Assert.Contains("host", ex.Message);
    }

    [Fact]
    public void ANewPoint_TakesTypedFields_AndTags()
    {
        var values = new Dictionary<string, object?> { ["time"] = "2024-05-01T12:00:00Z", ["host"] = "web-1", ["usage"] = "2.5", ["cores"] = "8", ["note"] = "ok" };
        var point = LineProtocolRecords.BuildPoint(Points(), values, RecordValues.ParseTime("2024-05-01T12:00:00Z"));
        Assert.Equal("web-1", point.Tags["host"]);
        Assert.Equal(2.5, point.Fields["usage"]);
        Assert.Equal(8L, point.Fields["cores"]);
        Assert.Equal("ok", point.Fields["note"]);
        Assert.Equal(new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc), point.TimeStamp);
    }

    [Fact]
    public void AFieldThatDoesNotFitItsType_SaysSo()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LineProtocolRecords.BuildPoint(Points(),
            new Dictionary<string, object?> { ["usage"] = "lots" }, DateTime.UtcNow));
        Assert.Contains("not a valid float", ex.Message);
        Assert.Throws<InvalidOperationException>(() => LineProtocolRecords.BuildPoint(Points(), new Dictionary<string, object?> { ["host"] = "a" }, DateTime.UtcNow));   // no field
    }

    [Fact]
    public async Task ChangingATimeOrTag_IsRefused_BeforeAnythingIsSent()
    {
        var result = await LineProtocolRecords.UpdateAsync(null!, "db", Points(), true,
            new Dictionary<string, object> { ["time"] = DateTime.UtcNow, ["host"] = "a" }, new Dictionary<string, object> { ["host"] = "b" });
        Assert.False(result.Success);
        Assert.Contains("cannot be changed", result.Body);
    }

    [Theory]
    [InlineData("2024-05-01T12:30:00Z", 2024, 5, 1, 12, 30)]
    [InlineData("2024-05-01 12:30", 2024, 5, 1, 12, 30)]
    public void Times_AreReadAsUtc(string text, int y, int mo, int d, int h, int mi)
    {
        var time = RecordValues.ParseTime(text);
        Assert.Equal(new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Utc), time);
        Assert.Throws<InvalidOperationException>(() => RecordValues.ParseTime("yesterday-ish"));
    }

    [Fact]
    public void ValueText_RoundTripsForTheEditBox()
    {
        Assert.Equal("", RecordValues.ToText(null));
        Assert.Equal("1.5", RecordValues.ToText(1.5));
        Assert.Equal("true", RecordValues.ToText(true));
        Assert.Equal("2024-05-01T12:30:00Z", RecordValues.ToText(new DateTime(2024, 5, 1, 12, 30, 0, DateTimeKind.Utc)));
        Assert.True(RecordValues.SameValue(1.5, "1.5"));
        Assert.True(RecordValues.SameValue(null, null));
        Assert.False(RecordValues.SameValue("a", "b"));
    }
}
