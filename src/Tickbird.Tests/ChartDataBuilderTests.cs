using Tickbird.Core.Charting;
using Tickbird.Core.Data;

namespace Tickbird.Tests;

public class ChartDataBuilderTests
{
    private static ResultSeries Series(string? name, string[] columns, params object?[][] rows) =>
        Series(name, columns, new Dictionary<string, string>(), rows);

    private static ResultSeries Series(string? name, string[] columns, Dictionary<string, string> tags, params object?[][] rows) =>
        new(name!, columns.ToList(), tags, rows.Select(r => (IList<object>)r.Cast<object>().ToList()).ToList());

    private static DateTime T(int minute) => new(2024, 1, 2, 3, minute, 0, DateTimeKind.Utc);

    [Fact]
    public void InfluxQl_DateTimeTime_OneLinePerNumericField()
    {
        var data = ChartDataBuilder.Build(new[]
        {
            Series("cpu", new[] { "time", "usage", "idle" },
                new object?[] { T(0), 41.5, 58.5 },
                new object?[] { T(1), 12.25, 87.75 })
        });

        Assert.Equal(ChartAxisKind.Time, data.AxisKind);
        Assert.Equal(new[] { "usage", "idle" }, data.Fields);
        Assert.Equal(2, data.Series.Count);
        Assert.Equal("usage", data.Series[0].Label);
        Assert.Equal(new[] { T(0).ToOADate(), T(1).ToOADate() }, data.Series[0].Xs);
        Assert.Equal(new[] { 41.5, 12.25 }, data.Series[0].Ys);
        Assert.Equal("time", data.XColumn);
        Assert.Null(data.Message);
    }

    [Fact]
    public void Rfc3339Strings_AreReadAsTime()
    {
        var data = ChartDataBuilder.Build(new[]
        {
            Series("m", new[] { "time", "v" }, new object?[] { "2024-01-02T03:04:05Z", 1L }, new object?[] { "2024-01-02T03:04:06Z", 2L })
        });

        Assert.Equal(ChartAxisKind.Time, data.AxisKind);
        Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc), ChartValues.FromX(data.Series[0].Xs[0]), TimeSpan.FromMilliseconds(1));
    }

    [Theory]
    [InlineData(1704164645L)]                 // seconds
    [InlineData(1704164645000L)]              // milliseconds
    [InlineData(1704164645000000L)]           // microseconds
    [InlineData(1704164645000000000L)]        // nanoseconds (InfluxDB 3 Arrow timestamps)
    public void EpochTimes_InferTheirUnit(long epoch)
    {
        Assert.True(ChartValues.TryGetUtc(epoch, out var utc));
        Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc), utc);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
    }

    [Fact]
    public void Sql_FlatTable_SkipsTextColumns_AndOffersThemForSplit()
    {
        var ns = (long)(T(0) - DateTime.UnixEpoch).Ticks * 100;
        var minute = 60_000_000_000L;
        var data = ChartDataBuilder.Build(new[]
        {
            Series(null, new[] { "time", "host", "speed" },
                new object?[] { ns, "truck-1", 50.0 },
                new object?[] { ns, "truck-2", 70.0 },
                new object?[] { ns + minute, "truck-1", 55.0 },
                new object?[] { ns + minute, "truck-2", 72.0 })
        });

        Assert.Equal(ChartAxisKind.Time, data.AxisKind);
        Assert.Equal(new[] { "speed" }, data.Fields);
        Assert.Equal(new[] { "host" }, data.SkippedColumns);
        Assert.Equal(new[] { "host" }, data.SplitCandidates);
        Assert.Single(data.Series);
        Assert.Equal(4, data.Series[0].Xs.Length);
    }

    [Fact]
    public void SplitBy_GivesOneLinePerValue_Sorted()
    {
        var data = ChartDataBuilder.Build(new[]
        {
            Series(null, new[] { "time", "host", "speed" },
                new object?[] { T(0), "truck-2", 70.0 },
                new object?[] { T(0), "truck-1", 50.0 },
                new object?[] { T(1), "truck-1", 55.0 },
                new object?[] { T(1), "truck-2", 72.0 })
        }, new ChartOptions { SplitBy = "HOST" });

        Assert.Equal(2, data.Series.Count);
        Assert.Equal("speed {host=truck-1}", data.Series[0].Label);
        Assert.Equal(new[] { 50.0, 55.0 }, data.Series[0].Ys);
        Assert.Equal("speed {host=truck-2}", data.Series[1].Label);
        Assert.Equal(new[] { 70.0, 72.0 }, data.Series[1].Ys);
    }

    [Fact]
    public void NumericLookingStrings_AreNotPlotted()
    {
        // Tag values such as vehicle IDs often look like numbers; they must not become a line.
        var data = ChartDataBuilder.Build(new[]
        {
            Series("m", new[] { "time", "vehicle", "v" }, new object?[] { T(0), "101", 1.0 }, new object?[] { T(1), "102", 2.0 })
        });

        Assert.Equal(new[] { "v" }, data.Fields);
        Assert.Contains("vehicle", data.SkippedColumns);
    }

    [Fact]
    public void GroupByTag_CombinedResults_LabelEachLineWithItsTags()
    {
        var data = ChartDataBuilder.Build(new[]
        {
            Series("cpu", new[] { "time", "mean" }, new Dictionary<string, string> { ["host"] = "a" },
                new object?[] { T(0), 1.0 }, new object?[] { T(1), 2.0 }),
            Series("cpu", new[] { "time", "mean" }, new Dictionary<string, string> { ["host"] = "b" },
                new object?[] { T(0), 3.0 }, new object?[] { T(1), 4.0 })
        });

        Assert.Equal(new[] { "mean {host=a}", "mean {host=b}" }, data.Series.Select(s => s.Label));
    }

    [Fact]
    public void DifferentMeasurements_IncludeTheNameInTheLabel()
    {
        var data = ChartDataBuilder.Build(new[]
        {
            Series("cpu", new[] { "time", "v" }, new object?[] { T(0), 1.0 }, new object?[] { T(1), 1.0 }),
            Series("mem", new[] { "time", "v" }, new object?[] { T(0), 2.0 }, new object?[] { T(1), 2.0 })
        });

        Assert.Equal(new[] { "cpu.v", "mem.v" }, data.Series.Select(s => s.Label));
    }

    [Fact]
    public void Nulls_BecomeGaps_AndBooleansBecomeZeroOrOne()
    {
        var data = ChartDataBuilder.Build(new[]
        {
            Series("m", new[] { "time", "v", "on" },
                new object?[] { T(0), 1.0, true },
                new object?[] { T(1), null, false },
                new object?[] { T(2), 3.0, null })
        });

        var v = data.Series.Single(s => s.Field == "v");
        Assert.True(double.IsNaN(v.Ys[1]));
        Assert.Equal(new[] { 1.0, 0.0 }, data.Series.Single(s => s.Field == "on").Ys.Take(2));
    }

    [Fact]
    public void DescendingTime_IsSorted()
    {
        var data = ChartDataBuilder.Build(new[]
        {
            Series("m", new[] { "time", "v" }, new object?[] { T(2), 3.0 }, new object?[] { T(1), 2.0 }, new object?[] { T(0), 1.0 })
        });

        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, data.Series[0].Ys);
        Assert.True(data.Series[0].Xs[0] < data.Series[0].Xs[2]);
    }

    [Fact]
    public void HiddenFields_AreListedButNotDrawn()
    {
        var data = ChartDataBuilder.Build(new[]
        {
            Series("m", new[] { "time", "a", "b" }, new object?[] { T(0), 1.0, 2.0 }, new object?[] { T(1), 1.0, 2.0 })
        }, new ChartOptions { HiddenFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A" } });

        Assert.Equal(new[] { "a", "b" }, data.Fields);
        Assert.Equal(new[] { "b" }, data.Series.Select(s => s.Field));
    }

    [Fact]
    public void AllFieldsHidden_ExplainsWhyNothingIsDrawn()
    {
        var data = ChartDataBuilder.Build(new[]
        {
            Series("m", new[] { "time", "a" }, new object?[] { T(0), 1.0 }, new object?[] { T(1), 1.0 })
        }, new ChartOptions { HiddenFields = new HashSet<string> { "a" } });

        Assert.Equal(ChartAxisKind.None, data.AxisKind);
        Assert.Contains("hidden", data.Message);
    }

    [Fact]
    public void NoTimeColumn_GivesBarsByFirstTextColumn()
    {
        var data = ChartDataBuilder.Build(new[]
        {
            Series("counts", new[] { "host", "count" }, new object?[] { "a", 10L }, new object?[] { "b", 20L })
        });

        Assert.Equal(ChartAxisKind.Category, data.AxisKind);
        Assert.Equal(new[] { "a", "b" }, data.Categories);
        Assert.Equal(new[] { 10.0, 20.0 }, data.Series[0].Ys);
        Assert.Equal("host", data.XColumn);
    }

    [Fact]
    public void AggregateWithoutGroupByTime_GivesOneBarPerTagSet()
    {
        // InfluxQL returns time = 1970-01-01 for every row of an aggregate without GROUP BY time.
        var epoch = "1970-01-01T00:00:00Z";
        var data = ChartDataBuilder.Build(new[]
        {
            Series("cpu", new[] { "time", "mean" }, new Dictionary<string, string> { ["host"] = "a" }, new object?[] { epoch, 1.5 }),
            Series("cpu", new[] { "time", "mean" }, new Dictionary<string, string> { ["host"] = "b" }, new object?[] { epoch, 2.5 })
        });

        Assert.Equal(ChartAxisKind.Category, data.AxisKind);
        Assert.Equal(new[] { "{host=a}", "{host=b}" }, data.Categories);
        Assert.Equal(new[] { 1.5, 2.5 }, data.Series[0].Ys);
    }

    [Fact]
    public void NoNumericColumns_ReturnsAMessage()
    {
        var data = ChartDataBuilder.Build(new[]
        {
            Series("measurements", new[] { "name" }, new object?[] { "cpu" }, new object?[] { "mem" })
        });

        Assert.Equal(ChartAxisKind.None, data.AxisKind);
        Assert.Contains("no numeric columns", data.Message);
        Assert.Empty(data.Series);
    }

    [Fact]
    public void EmptyResult_ReturnsAMessage()
    {
        var data = ChartDataBuilder.Build(new[] { Series("m", new[] { "time", "v" }) });
        Assert.Equal(ChartAxisKind.None, data.AxisKind);
        Assert.NotNull(data.Message);
    }

    [Fact]
    public void LongSeries_AreDownsampled()
    {
        var rows = Enumerable.Range(0, 20_000).Select(i => new object?[] { T(0).AddSeconds(i), Math.Sin(i / 100.0) }).ToArray();
        var data = ChartDataBuilder.Build(new[] { Series("m", new[] { "time", "v" }, rows) }, new ChartOptions { MaxPointsPerSeries = 1000 });

        var line = data.Series.Single();
        Assert.True(line.IsDownsampled);
        Assert.Equal(20_000, line.SourcePointCount);
        Assert.InRange(line.Xs.Length, 900, 1000);
        Assert.True(data.IsDownsampled);
    }

    [Fact]
    public void TooManyLines_AreCapped_WithAMessage()
    {
        var rows = Enumerable.Range(0, 30).SelectMany(h => new[]
        {
            new object?[] { T(0), "h" + h, 1.0 }, new object?[] { T(1), "h" + h, 2.0 }
        }).ToArray();
        var data = ChartDataBuilder.Build(new[] { Series(null, new[] { "time", "host", "v" }, rows) },
            new ChartOptions { SplitBy = "host", MaxSeries = 10 });

        Assert.Equal(10, data.Series.Count);
        Assert.Contains("10 of 30", data.Message);
    }
}

public class DownsamplerTests
{
    [Fact]
    public void ShortInput_IsReturnedUnchanged()
    {
        var xs = new[] { 1.0, 2, 3 };
        var ys = new[] { 4.0, 5, 6 };
        var (ox, oy) = Downsampler.Lttb(xs, ys, 10);
        Assert.Same(xs, ox);
        Assert.Same(ys, oy);
    }

    [Fact]
    public void KeepsFirstLast_AndThePeak()
    {
        var xs = Enumerable.Range(0, 1000).Select(i => (double)i).ToArray();
        var ys = xs.Select(x => x == 500 ? 100.0 : 0.0).ToArray();
        var (ox, oy) = Downsampler.Lttb(xs, ys, 50);

        Assert.Equal(50, ox.Length);
        Assert.Equal(0, ox[0]);
        Assert.Equal(999, ox[^1]);
        Assert.Contains(100.0, oy);
        Assert.True(ox.Zip(ox.Skip(1)).All(p => p.First < p.Second), "output must stay sorted");
    }

    [Fact]
    public void PreservesGaps()
    {
        var xs = Enumerable.Range(0, 2000).Select(i => (double)i).ToArray();
        var ys = xs.Select(x => x is >= 1000 and < 1010 ? double.NaN : 1.0).ToArray();
        var (ox, oy) = Downsampler.Lttb(xs, ys, 100);

        Assert.Single(oy, double.IsNaN);
        var gap = Array.FindIndex(oy, double.IsNaN);
        Assert.True(ox[gap - 1] < 1000 && ox[gap + 1] >= 1010);
        Assert.InRange(ox.Length, 90, 102);
    }

    [Fact]
    public void AllNaN_ReturnsEmpty()
    {
        var xs = Enumerable.Range(0, 100).Select(i => (double)i).ToArray();
        var ys = xs.Select(_ => double.NaN).ToArray();
        var (ox, _) = Downsampler.Lttb(xs, ys, 10);
        Assert.Empty(ox);
    }
}
