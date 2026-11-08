using Tickbird.Core.Data;
using Tickbird.Core.Mapping;

namespace Tickbird.Tests;

public class MapTrackBuilderTests
{
    private static DateTime T(int s) => new(2026, 10, 21, 7, 11, s, DateTimeKind.Utc);

    private static ResultSeries Gps(params (double Lat, double Lon, object? State)[] rows) =>
        new("gps", new List<string> { "time", "latitude", "longitude", "state" }, new Dictionary<string, string>(),
            rows.Select((r, i) => (IList<object>)new List<object> { T(i), r.Lat, r.Lon, r.State! }).ToList());

    [Fact]
    public void DetectsLatitudeAndLongitudeColumns()
    {
        var data = MapTrackBuilder.Build(new[] { Gps((52.1, 21.0, "work"), (52.2, 21.1, "work")) }, new MapOptions());
        Assert.Equal("latitude", data.LatitudeColumn);
        Assert.Equal("longitude", data.LongitudeColumn);
        Assert.Single(data.Tracks);
        Assert.Equal(2, data.PointCount);
        Assert.Equal(MapColorMode.Single, data.ColorMode);
        Assert.Equal(new MapBounds(52.1, 21.0, 52.2, 21.1), data.Bounds);
    }

    [Fact]
    public void DetectsShortNames_AndNumericStrings()
    {
        var series = new ResultSeries("p", new List<string> { "time", "gps_lat", "gps_lng" }, new Dictionary<string, string>(),
            new List<IList<object>> { new List<object> { T(0), "52.5", "13.4" }, new List<object> { T(1), "52.6", "13.5" } });
        var data = MapTrackBuilder.Build(new[] { series }, new MapOptions());
        Assert.Equal("gps_lat", data.LatitudeColumn);
        Assert.Equal("gps_lng", data.LongitudeColumn);
        Assert.Equal(2, data.PointCount);
    }

    [Fact]
    public void SkipsMissingOutOfRangeAndZeroPositions()
    {
        var data = MapTrackBuilder.Build(new[] { Gps((52, 21, "a"), (0, 0, "a"), (95, 21, "a"), (double.NaN, 21, "a"), (52.1, 21.1, "a")) }, new MapOptions());
        Assert.Equal(2, data.PointCount);
        Assert.Equal(3, data.SkippedRows);
    }

    [Fact]
    public void TextColorColumn_GivesOneLegendEntryPerValue()
    {
        var data = MapTrackBuilder.Build(new[] { Gps((52, 21, "work"), (52.1, 21.1, "turn"), (52.2, 21.2, "work")) },
            new MapOptions { ColorColumn = "state" });
        Assert.Equal(MapColorMode.Categories, data.ColorMode);
        Assert.Equal(new[] { "work", "turn" }, data.Legend.Select(l => l.Label));
        Assert.Equal(new[] { 0, 1, 0 }, data.Tracks[0].Points.Select(p => p.Legend));
        Assert.Equal(2, data.Legend[0].Count);
    }

    [Fact]
    public void ManyNumericValues_AreGroupedIntoRanges()
    {
        var rows = Enumerable.Range(0, 20).Select(i => (52 + i / 1000.0, 21 + i / 1000.0, (object?)(double)i)).ToArray();
        var data = MapTrackBuilder.Build(new[] { Gps(rows) }, new MapOptions { ColorColumn = "state", RangeCount = 4 });
        Assert.Equal(MapColorMode.Ranges, data.ColorMode);
        Assert.Equal(4, data.Legend.Count);
        Assert.Equal(0, data.Tracks[0].Points[0].Legend);
        Assert.Equal(3, data.Tracks[0].Points[^1].Legend);
        Assert.All(data.Legend, l => Assert.StartsWith("#", l.DefaultColor));
    }

    [Fact]
    public void ColorColumnHoldingColors_UsesThem()
    {
        var data = MapTrackBuilder.Build(new[] { Gps((52, 21, "#FFD400"), (52.1, 21.1, "#ff6d00")) }, new MapOptions { ColorColumn = "state" });
        Assert.Equal(MapColorMode.Colors, data.ColorMode);
        Assert.Equal(new[] { "#FFD400", "#FF6D00" }, data.Legend.Select(l => l.DefaultColor));
    }

    [Fact]
    public void SplitBy_DrawsOneTrackPerValue()
    {
        var data = MapTrackBuilder.Build(new[] { Gps((52, 21, "A"), (52.1, 21.1, "B"), (52.2, 21.2, "A")) }, new MapOptions { SplitBy = "state" });
        Assert.Equal(new[] { "A", "B" }, data.Tracks.Select(t => t.Label));
        Assert.Equal(2, data.Tracks[0].Points.Length);
    }

    [Fact]
    public void ExplicitBindingWins_AndMissingColumnsExplain()
    {
        var series = new ResultSeries("m", new List<string> { "time", "a", "b" }, new Dictionary<string, string>(),
            new List<IList<object>> { new List<object> { T(0), 10.0, 20.0 } });
        var auto = MapTrackBuilder.Build(new[] { series }, new MapOptions());
        Assert.Null(auto.LatitudeColumn);
        Assert.NotNull(auto.Message);
        Assert.Equal(new[] { "a", "b" }, auto.CoordinateColumns);

        var bound = MapTrackBuilder.Build(new[] { series }, new MapOptions { LatitudeColumn = "a", LongitudeColumn = "b" });
        Assert.Equal(1, bound.PointCount);
        Assert.Equal(10.0, bound.Tracks[0].Points[0].Latitude);
    }

    private static ResultSeries Machine() =>
        new("gps", new List<string> { "time", "lat", "lon", "state", "speed" }, new Dictionary<string, string>(),
            new List<IList<object>>
            {
                new List<object> { T(0), 52.0, 21.0, "work", 6.5 },
                new List<object> { T(1), 52.1, 21.1, "work", 1.0 },
                new List<object> { T(2), 52.2, 21.2, "turn", 7.0 },
                new List<object> { T(3), 52.3, 21.3, "idle", 0.0 },
            });

    private static MapCondition If(string column, MapConditionOperator op, string? value = null) => new() { Column = column, Operator = op, Value = value };

    [Fact]
    public void Rules_CombineSeveralFields_FirstMatchWins()
    {
        var rules = new List<MapColorRule>
        {
            new() { Color = "#FFD400", Conditions = { If("state", MapConditionOperator.Equals, "WORK"), If("speed", MapConditionOperator.Greater, "3") } },
            new() { Color = "#FF6D00", Label = "Turning or slow", Conditions = { If("speed", MapConditionOperator.LessOrEqual, "7") } },
        };
        var data = MapTrackBuilder.Build(new[] { Machine() }, new MapOptions { ColorMode = MapColorMode.Rules, Rules = rules });
        Assert.Equal(MapColorMode.Rules, data.ColorMode);
        Assert.Null(data.ColorColumn);
        Assert.Equal(new[] { "state = WORK and speed > 3", "Turning or slow" }, data.Legend.Select(l => l.Label));
        Assert.Equal(new[] { "#FFD400", "#FF6D00" }, data.Legend.Select(l => l.DefaultColor));
        // work@6.5 -> rule 1; work@1 -> rule 2; turn@7 -> rule 2; idle@0 -> rule 2
        Assert.Equal(new[] { 0, 1, 1, 1 }, data.Tracks[0].Points.Select(p => p.Legend));
    }

    [Fact]
    public void Rules_UnmatchedPointsUseOtherColor()
    {
        var rules = new List<MapColorRule> { new() { Color = "#00E5FF", Conditions = { If("state", MapConditionOperator.Equals, "turn") } } };
        var data = MapTrackBuilder.Build(new[] { Machine() }, new MapOptions { ColorMode = MapColorMode.Rules, Rules = rules, OtherColor = "#123456" });
        Assert.Equal(new[] { "state = turn", "Other" }, data.Legend.Select(l => l.Label));
        Assert.Equal("#123456", data.Legend[1].DefaultColor);
        Assert.Equal(new[] { 1, 1, 0, 1 }, data.Tracks[0].Points.Select(p => p.Legend));
    }

    [Theory]
    [InlineData(MapConditionOperator.Equals, "6.50", 6.5, true)]
    [InlineData(MapConditionOperator.NotEquals, "6.5", 6.5, false)]
    [InlineData(MapConditionOperator.GreaterOrEqual, "6.5", 6.5, true)]
    [InlineData(MapConditionOperator.Less, "10", 6.5, true)]
    [InlineData(MapConditionOperator.IsEmpty, null, 6.5, false)]
    [InlineData(MapConditionOperator.IsNotEmpty, null, 6.5, true)]
    public void Condition_ComparesNumbersAsNumbers(MapConditionOperator op, string? value, double cell, bool expected) =>
        Assert.Equal(expected, If("speed", op, value).Matches(cell));

    [Fact]
    public void Condition_ComparesTextIgnoringCase()
    {
        Assert.True(If("state", MapConditionOperator.Contains, "OR").Matches("work"));
        Assert.True(If("state", MapConditionOperator.Equals, "Work").Matches("work"));
        Assert.True(If("state", MapConditionOperator.IsEmpty).Matches(null));
        Assert.True(If("state", MapConditionOperator.NotEquals, "work").Matches(null));
        Assert.True(If("on", MapConditionOperator.Equals, "true").Matches(true));
    }

    private static ResultSeries Harvester() =>
        new("gps", new List<string> { "time", "lat", "lon", "operatingmode", "depthguidance", "bunkerunload" }, new Dictionary<string, string>(),
            new List<IList<object>>
            {
                new List<object> { T(0), 52.0, 21.0, 2L, "on", false },
                new List<object> { T(1), 52.1, 21.1, 2L, "off", true },
                new List<object> { T(2), 52.2, 21.2, 1L, "on", true },
                new List<object> { T(3), 52.3, 21.3, 3L, "off", false },
            });

    [Fact]
    public void Layers_AreIndependent_AndAPointCanBeInSeveral()
    {
        var layers = new List<MapLayer>
        {
            new() { Name = "Mode 2", Conditions = { If("operatingmode", MapConditionOperator.Equals, "2") } },
            new() { Name = "Depth guidance", Style = MapLayerStyle.Halo, Conditions = { If("depthguidance", MapConditionOperator.Equals, "on") } },
            new() { Name = "Unload", Style = MapLayerStyle.Points, Conditions = { If("bunkerunload", MapConditionOperator.Equals, "true") } },
            new() { Name = "Being edited", Conditions = { new MapCondition() } },
        };
        var data = MapTrackBuilder.Build(new[] { Harvester() }, new MapOptions { Layers = layers });
        var points = data.Tracks[0].Points;
        Assert.Equal(4, data.Layers.Count);
        Assert.Equal(new[] { 2, 2, 2, 0 }, data.LayerCounts);
        Assert.True(points[0].InLayer(0) && points[0].InLayer(1) && !points[0].InLayer(2));
        Assert.True(points[1].InLayer(0) && !points[1].InLayer(1) && points[1].InLayer(2));
        Assert.True(!points[2].InLayer(0) && points[2].InLayer(1) && points[2].InLayer(2));
        Assert.Equal(0UL, points[3].Layers);
        // Layers do not change the base coloring.
        Assert.Equal(MapColorMode.Single, data.ColorMode);
    }

    [Fact]
    public void Layers_WorkTogetherWithColorBy()
    {
        var layers = new List<MapLayer> { new() { Conditions = { If("bunkerunload", MapConditionOperator.Equals, "true") } } };
        var data = MapTrackBuilder.Build(new[] { Harvester() }, new MapOptions { ColorColumn = "depthguidance", Layers = layers });
        Assert.Equal(MapColorMode.Categories, data.ColorMode);
        Assert.Equal(new[] { "on", "off" }, data.Legend.Select(l => l.Label));
        Assert.Equal("bunkerunload = true", data.Layers[0].Describe());
        Assert.Equal(2, data.LayerCounts[0]);
    }

    [Fact]
    public void DistinctValues_ListsEachValueOnce()
    {
        Assert.Equal(new[] { "2", "1", "3" }, MapTrackBuilder.DistinctValues(new[] { Harvester() }, "operatingmode"));
    }

    [Fact]
    public void Gradient_RunsGreenToRed()
    {
        Assert.Equal("#2ECC40", MapTrackBuilder.Gradient(0));
        Assert.Equal("#FFDC00", MapTrackBuilder.Gradient(0.5));
        Assert.Equal("#FF4136", MapTrackBuilder.Gradient(1));
    }
}
