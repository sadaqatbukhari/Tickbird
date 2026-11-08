using Tickbird.Core.Data;
using Tickbird.Core.Mapping;
using Tickbird.Core.Query;
using Tickbird.Core.Settings;

namespace Tickbird.Tests;

public class RowFilterTests
{
    private static ResultSeries Rows() =>
        new("gps", new List<string> { "time", "state", "speed", "operatingmode" }, new Dictionary<string, string> { ["vehicle"] = "6S2495" },
            new List<IList<object>>
            {
                new List<object> { new DateTime(2026, 10, 21, 7, 0, 0, DateTimeKind.Utc), "work", 6.5, 2L },
                new List<object> { new DateTime(2026, 10, 21, 7, 0, 1, DateTimeKind.Utc), "turn", 1.0, 2L },
                new List<object> { new DateTime(2026, 10, 21, 7, 0, 2, DateTimeKind.Utc), "work", 7.25, 1L },
                new List<object> { new DateTime(2026, 10, 21, 7, 0, 3, DateTimeKind.Utc), null!, 0.0, 3L },
            });

    private static MapCondition If(string column, MapConditionOperator op, string? value = null) => new() { Column = column, Operator = op, Value = value };

    private static readonly ResultFormatter Formatter = new(TickbirdSettings.DateFormatMonth, TickbirdSettings.TimeFormat24Hour);

    [Fact]
    public void NoFilter_ReturnsTheSameResult()
    {
        var rows = Rows();
        Assert.False(new RowFilter().IsActive);
        Assert.Same(rows, new RowFilter().Apply(rows));
        Assert.Same(rows, new RowFilter { Conditions = { new MapCondition() } }.Apply(rows));   // incomplete condition
    }

    [Fact]
    public void Conditions_AreJoinedWithAnd()
    {
        var filter = new RowFilter { Conditions = { If("state", MapConditionOperator.Equals, "WORK"), If("speed", MapConditionOperator.Greater, "7") } };
        Assert.Equal(new[] { 2 }, filter.MatchingRows(Rows()));
        Assert.Equal("state = WORK and speed > 7", filter.Describe());
    }

    [Fact]
    public void Conditions_HandleNullsAndMissingColumns()
    {
        Assert.Equal(new[] { 3 }, new RowFilter { Conditions = { If("state", MapConditionOperator.IsEmpty) } }.MatchingRows(Rows()));
        Assert.Equal(new[] { 0, 1, 3 }, new RowFilter { Conditions = { If("state", MapConditionOperator.NotEquals, "work"), If("speed", MapConditionOperator.Less, "7") } }
            .MatchingRows(Rows()).Concat(new RowFilter { Conditions = { If("state", MapConditionOperator.Equals, "work"), If("speed", MapConditionOperator.Less, "7") } }.MatchingRows(Rows())).OrderBy(i => i));
        Assert.Empty(new RowFilter { Conditions = { If("nosuchcolumn", MapConditionOperator.Equals, "x") } }.MatchingRows(Rows()));
    }

    [Fact]
    public void QuickText_MatchesAnyCell_EveryWordRequired()
    {
        Assert.Equal(new[] { 1 }, new RowFilter { Text = "turn" }.MatchingRows(Rows(), Formatter));
        Assert.Equal(new[] { 2 }, new RowFilter { Text = "work 7.25" }.MatchingRows(Rows(), Formatter));
        Assert.Equal(4, new RowFilter { Text = "6s2495" }.MatchingRows(Rows(), Formatter).Count);   // a GROUP BY tag
        Assert.Empty(new RowFilter { Text = "harvest" }.MatchingRows(Rows(), Formatter));
    }

    [Fact]
    public void Apply_KeepsColumnsTagsAndOrder()
    {
        var filtered = new RowFilter { Conditions = { If("operatingmode", MapConditionOperator.Equals, "2") } }.Apply(Rows());
        Assert.Equal(2, filtered.Values.Count);
        Assert.Equal("work", filtered.Values[0][1]);
        Assert.Equal("turn", filtered.Values[1][1]);
        Assert.Equal(Rows().Columns, filtered.Columns);
        Assert.Equal("6S2495", filtered.Tags["vehicle"]);
    }

    [Fact]
    public void Copy_IsIndependent()
    {
        var filter = new RowFilter { Text = "a", Conditions = { If("state", MapConditionOperator.Equals, "work") } };
        var copy = filter.Copy();
        filter.Conditions[0].Value = "turn";
        Assert.Equal("work", copy.Conditions[0].Value);
    }
}
