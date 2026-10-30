using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Tickbird.Core.Charting;
using Tickbird.Core.Data;
using Tickbird.Core.Settings;
using Tickbird.Desktop.Services;
using Tickbird.Desktop.Views.Requests;

namespace Tickbird.Desktop.Tests;

public sealed class ChartViewTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "tickbird-chart-" + Guid.NewGuid().ToString("N"));

    public ChartViewTests()
    {
        TickbirdApp.Current = new TickbirdApp(new SettingsStore(Path.Combine(dir, "settings.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    private static DateTime T(int minute) => new(2024, 1, 2, 3, minute, 0, DateTimeKind.Utc);

    private static ResultSeries Cpu(string host, double offset) =>
        new("cpu", new List<string> { "time", "usage", "idle" }, new Dictionary<string, string> { ["host"] = host },
            Enumerable.Range(0, 30).Select(i => (IList<object>)new List<object> { T(i), offset + Math.Sin(i / 4.0) * 10 + 40, 60 - offset }).ToList());

    private static (ResultsView View, Window Host) Show(ResultSeries series, ChartSettings? settings = null, IReadOnlyList<ResultSeries>? all = null)
    {
        var view = new ResultsView { ChartSettings = settings ?? new ChartSettings(), AllResults = all };
        var host = new Window { Content = view, Width = 1000, Height = 520 };
        host.Show();
        view.Show(series);
        return (view, host);
    }

    [AvaloniaFact]
    public async Task ChartButton_SwitchesToChart_AndDrawsOneLinePerField()
    {
        var (view, host) = Show(Cpu("a", 0));
        Assert.False(view.IsChartMode);
        Assert.Null(view.Chart);                       // the chart is only built when first used

        view.SetChartMode(true);
        await view.Chart!.RefreshAsync();

        Assert.True(view.IsChartMode);
        Assert.Equal(ChartAxisKind.Time, view.Chart.Data!.AxisKind);
        Assert.Equal(2, view.Chart.Plot.Plot.GetPlottables<ScottPlot.Plottables.Scatter>().Count());

        view.SetChartMode(false);
        Assert.False(view.IsChartMode);
        host.Close();
    }

    [AvaloniaFact]
    public void ChartChoice_IsSharedByTheQueryTabsResults()
    {
        var settings = new ChartSettings();
        var (first, host1) = Show(Cpu("a", 0), settings);
        first.SetChartMode(true);

        // A result shown later (another result tab, or the next run of the query) opens as a chart too.
        var (second, host2) = Show(Cpu("b", 5), settings);
        Assert.True(second.IsChartMode);
        host1.Close();
        host2.Close();
    }

    [AvaloniaFact]
    public async Task GroupByTag_Results_AreCombined_WithTagLabels()
    {
        var a = Cpu("a", 0);
        var b = Cpu("b", 5);
        var settings = new ChartSettings { ShowChart = true };
        settings.SetFieldVisible("idle", false);
        var (view, host) = Show(a, settings, new[] { a, b });
        await view.Chart!.RefreshAsync();

        Assert.Equal(new[] { "usage {host=a}", "usage {host=b}" }, view.Chart.Data!.Series.Select(s => s.Label));

        settings.CombineResults = false;
        await view.Chart.RefreshAsync();
        Assert.Single(view.Chart.Data!.Series);
        host.Close();
    }

    [AvaloniaFact]
    public async Task ResultWithoutTime_IsDrawnAsBars()
    {
        var counts = new ResultSeries("counts", new List<string> { "host", "count" }, new Dictionary<string, string>(),
            new List<IList<object>> { new List<object> { "web-1", 10L }, new List<object> { "web-2", 25L } });
        var (view, host) = Show(counts, new ChartSettings { ShowChart = true });
        await view.Chart!.RefreshAsync();

        Assert.Equal(ChartAxisKind.Category, view.Chart.Data!.AxisKind);
        Assert.Single(view.Chart.Plot.Plot.GetPlottables<ScottPlot.Plottables.BarPlot>());
        host.Close();
    }

    [AvaloniaFact]
    public async Task TextOnlyResult_ExplainsWhyThereIsNoChart()
    {
        var names = new ResultSeries("measurements", new List<string> { "name" }, new Dictionary<string, string>(),
            new List<IList<object>> { new List<object> { "cpu" }, new List<object> { "mem" } });
        var (view, host) = Show(names, new ChartSettings { ShowChart = true });
        await view.Chart!.RefreshAsync();

        Assert.Equal(ChartAxisKind.None, view.Chart.Data!.AxisKind);
        Assert.False(view.Chart.Plot.IsVisible);
        host.Close();
    }

    [AvaloniaFact]
    public async Task LineColorAndWidth_CanBeSetPerLine_OrForAll()
    {
        var settings = new ChartSettings { ShowChart = true, LineWidth = 3 };
        settings.SetLineColor("usage", "#FF0000");
        settings.SetLineWidth("idle", 6);
        var series = new ResultSeries("cpu", new List<string> { "time", "usage", "idle" }, new Dictionary<string, string>(),
            Enumerable.Range(0, 10).Select(i => (IList<object>)new List<object> { T(i), (double)i, 10.0 - i }).ToList());
        var (view, host) = Show(series, settings);
        await view.Chart!.RefreshAsync();

        var lines = view.Chart.Plot.Plot.GetPlottables<ScottPlot.Plottables.Scatter>().ToDictionary(l => l.LegendText);
        Assert.Equal(ScottPlot.Color.FromHex("#FF0000"), lines["usage"].Color);
        Assert.Equal(3, lines["usage"].LineWidth);                    // follows the toolbar width
        Assert.Equal(6, lines["idle"].LineWidth);                     // its own width
        Assert.Equal(new ScottPlot.Palettes.Category10().GetColor(1), lines["idle"].Color);

        settings.Palette = "Dark";
        settings.SetLineColor("usage", null);
        await view.Chart.RefreshAsync();
        lines = view.Chart.Plot.Plot.GetPlottables<ScottPlot.Plottables.Scatter>().ToDictionary(l => l.LegendText);
        Assert.Equal(new ScottPlot.Palettes.Dark().GetColor(0), lines["usage"].Color);
        host.Close();
    }

    [Theory]
    [InlineData("#ff0000", "#FF0000")]
    [InlineData("00ff00", "#00FF00")]
    [InlineData("#00f", "#0000FF")]
    [InlineData("orange", "#FFA500")]
    [InlineData(" #80112233 ", "#80112233")]
    public void ColorText_IsParsed(string text, string expected)
    {
        Assert.True(ChartView.TryParseColor(text, out var color));
        Assert.Equal(expected, ChartView.ToHex(color));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a color")]
    [InlineData("#12")]
    public void BadColorText_IsRejected(string text) => Assert.False(ChartView.TryParseColor(text, out _));

    [Fact]
    public void LineStyleChanges_BumpTheSettingsVersion_OnlyWhenSomethingChanges()
    {
        var settings = new ChartSettings();
        var v = settings.Version;
        settings.SetLineColor("a", "#112233");
        Assert.Equal(v + 1, settings.Version);
        settings.SetLineColor("a", "#112233");
        Assert.Equal(v + 1, settings.Version);
        settings.SetLineWidth("a", 4);
        settings.SetLineColor("a", null);
        Assert.False(settings.LineColors.ContainsKey("a"));
        settings.ResetLineStyles();
        Assert.Empty(settings.LineWidths);
        Assert.Equal(v + 4, settings.Version);
        settings.Palette = "no such palette";
        Assert.Equal(ChartPalettes.Default, settings.Palette);
    }

    /// <summary>Renders a chart to PNG for visual review. Set INFLUX_SCREENSHOT_DIR to enable.</summary>
    [AvaloniaFact]
    public async Task RenderChart()
    {
        var target = Environment.GetEnvironmentVariable("INFLUX_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(target)) return;
        var a = Cpu("web-1", 0);
        var b = Cpu("web-2", 8);
        var styled = new ChartSettings { ShowChart = true, LineWidth = 2 };
        styled.SetLineColor("usage {host=web-1}", "#D62728");
        styled.SetLineWidth("usage {host=web-1}", 4);
        var (view, host) = Show(a, styled, new[] { a, b });
        await view.Chart!.RefreshAsync();
        host.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Directory.CreateDirectory(target);
        host.CaptureRenderedFrame()?.Save(Path.Combine(target, "chart.png"));
        host.Close();
    }
}
