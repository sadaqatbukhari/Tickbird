using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Tickbird.Core.Data;
using Tickbird.Core.Mapping;
using Tickbird.Core.Settings;
using Tickbird.Desktop.Services;
using Tickbird.Desktop.Views.Requests;

namespace Tickbird.Desktop.Tests;

public sealed class MapViewTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "tickbird-map-" + Guid.NewGuid().ToString("N"));

    public MapViewTests()
    {
        MapTiles.Enabled = false;   // no network in tests
        TickbirdApp.Current = new TickbirdApp(new SettingsStore(Path.Combine(dir, "settings.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    private static ResultSeries Track() =>
        new("gps", new List<string> { "time", "latitude", "longitude", "state" }, new Dictionary<string, string>(),
            Enumerable.Range(0, 40).Select(i => (IList<object>)new List<object>
            {
                new DateTime(2026, 10, 21, 7, 0, i, DateTimeKind.Utc), 52.0 + i * 0.0001, 21.0 + i * 0.0002, i % 10 < 7 ? "work" : "turn"
            }).ToList());

    private static (ResultsView View, Window Host) Show(ChartSettings settings)
    {
        var view = new ResultsView { ChartSettings = settings };
        var host = new Window { Content = view, Width = 1000, Height = 560 };
        host.Show();
        view.Show(Track());
        return (view, host);
    }

    [AvaloniaFact]
    public async Task MapToolbar_StaysCompact_AndOffersOverflowCommands()
    {
        var (view, host) = Show(new ChartSettings { Mode = ResultMode.Map });
        try
        {
            await view.Map!.RefreshAsync();
            host.Width = 650;
            host.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            host.UpdateLayout();
            var toolbar = Assert.Single(view.Map.GetVisualDescendants().OfType<ResponsiveToolBar>());
            Assert.Equal(26, toolbar.Bounds.Height);
            var more = Assert.Single(toolbar.GetVisualDescendants().OfType<Button>(), b => b.Name == "MoreCommands");
            Assert.True(more.IsVisible);
            var flyout = Assert.IsType<Flyout>(more.Flyout);
            flyout.ShowAt(more);
            host.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var menu = Assert.IsType<StackPanel>(Assert.IsType<ScrollViewer>(flyout.Content).Content);
            Assert.Contains(menu.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "Save PNG…");
            Assert.Contains(menu.GetVisualDescendants().OfType<CheckBox>(), b => b.Content as string == "Start/end");
            var target = Environment.GetEnvironmentVariable("INFLUX_SCREENSHOT_DIR");
            if (!string.IsNullOrEmpty(target))
            {
                Directory.CreateDirectory(target);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                host.CaptureRenderedFrame()?.Save(Path.Combine(target, "map-compact-toolbar.png"));
                TopLevel.GetTopLevel(menu)?.CaptureRenderedFrame()?.Save(Path.Combine(target, "map-overflow-menu.png"));
            }
            flyout.Hide();
        }
        finally { host.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(ResultMode.Map, 450)]
    [InlineData(ResultMode.Map, 650)]
    [InlineData(ResultMode.Chart, 450)]
    [InlineData(ResultMode.Chart, 650)]
    public async Task QueryResultToolbar_IsFullyVisibleAboveSurface(ResultMode mode, int height)
    {
        var query = new QueryView { EditorText = "SELECT * FROM candata LIMIT 100" };
        var host = new Window { Width = 760, Height = height, Content = query };
        host.Show();
        try
        {
            var view = new ResultsView { ChartSettings = new ChartSettings { Mode = mode } };
            view.Show(Track());
            var tabs = Assert.Single(query.GetVisualDescendants().OfType<TabControl>());
            tabs.Items.Add(new TabItem { Header = "Results 1", Content = view });
            tabs.SelectedIndex = 0;
            if (mode == ResultMode.Map) await view.Map!.RefreshAsync();
            else await view.Chart!.RefreshAsync();
            host.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            host.UpdateLayout();
            var toolbar = Assert.Single(view.GetVisualDescendants().OfType<ResponsiveToolBar>());
            Control surface = mode == ResultMode.Map ? view.Map!.Canvas : view.Chart!.Plot;
            var toolbarTop = toolbar.TranslatePoint(default, host)!.Value.Y;
            var surfaceTop = surface.TranslatePoint(default, host)!.Value.Y;
            var target = Environment.GetEnvironmentVariable("INFLUX_SCREENSHOT_DIR");
            if (!string.IsNullOrEmpty(target))
            {
                Directory.CreateDirectory(target);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                host.CaptureRenderedFrame()?.Save(Path.Combine(target, $"query-{mode}-{height}.png"));
            }
            Assert.Equal(26, toolbar.Bounds.Height);
            Assert.True(surfaceTop >= toolbarTop + 26 + 6, $"{mode} surface starts at {surfaceTop}, toolbar at {toolbarTop}, height {toolbar.Bounds.Height}");
        }
        finally { host.Close(); }
    }

    [AvaloniaFact]
    public async Task MapButton_ShowsTracks_FromDetectedColumns()
    {
        var (view, host) = Show(new ChartSettings());
        Assert.False(view.IsMapMode);
        view.SetMode(ResultMode.Map);
        Assert.True(view.IsMapMode);
        Assert.False(view.IsChartMode);
        await view.Map!.RefreshAsync();
        var data = view.Map.Data!;
        Assert.Equal("latitude", data.LatitudeColumn);
        Assert.Equal(40, data.PointCount);
        Assert.Same(data, view.Map.Canvas.Data);

        view.SetMode(ResultMode.Grid);
        Assert.False(view.IsMapMode);
        host.Close();
    }

    [AvaloniaFact]
    public async Task ColorColumn_AndPickedColors_AreSharedSettings()
    {
        var settings = new ChartSettings { Mode = ResultMode.Map };
        settings.Map.ColorColumn = "state";
        settings.Map.SetColor(MapSettings.Key("state", "turn"), "#FF6D00");
        var (view, host) = Show(settings);
        await view.Map!.RefreshAsync();
        var data = view.Map.Data!;
        Assert.Equal(MapColorMode.Categories, data.ColorMode);
        Assert.Equal(new[] { "work", "turn" }, data.Legend.Select(l => l.Label));
        Assert.True(view.IsMapMode);
        host.Close();
    }

    [AvaloniaFact]
    public async Task Rules_ColorByMoreThanOneField()
    {
        var settings = new ChartSettings { Mode = ResultMode.Map };
        settings.Map.ColorMode = MapColorMode.Rules;
        settings.Map.Rules.Add(new MapColorRule
        {
            Color = "#FF6D00",
            Conditions =
            {
                new MapCondition { Column = "state", Operator = MapConditionOperator.Equals, Value = "turn" },
                new MapCondition { Column = "latitude", Operator = MapConditionOperator.Greater, Value = "52.002" },
            }
        });
        var (view, host) = Show(settings);
        await view.Map!.RefreshAsync();
        var data = view.Map.Data!;
        Assert.Equal(MapColorMode.Rules, data.ColorMode);
        Assert.Equal("Other", data.Legend[^1].Label);
        Assert.Contains(data.Tracks[0].Points, p => p.Legend == 0);
        Assert.Contains(data.Tracks[0].Points, p => p.Legend == 1);
        host.Close();
    }

    [AvaloniaFact]
    public async Task Layers_FromSeveralFields_ShowTogether_AndCanBeHidden()
    {
        var settings = new ChartSettings { Mode = ResultMode.Map };
        settings.Map.Layers.Add(new MapLayer { Name = "Turning", Color = "#2979FF", Conditions = { new MapCondition { Column = "state", Value = "turn" } } });
        settings.Map.Layers.Add(new MapLayer
        {
            Name = "North part", Style = MapLayerStyle.Halo, Color = "#00E676",
            Conditions = { new MapCondition { Column = "latitude", Operator = MapConditionOperator.Greater, Value = "52.002" } }
        });
        var (view, host) = Show(settings);
        await view.Map!.RefreshAsync();
        var data = view.Map.Data!;
        Assert.Equal(2, data.Layers.Count);
        Assert.Contains(data.Tracks[0].Points, p => p.InLayer(0) && p.InLayer(1));
        host.CaptureRenderedFrame();   // draws base track, halo and line layers

        settings.Map.Layers[1].Visible = false;
        settings.Map.LayersChanged();
        await view.Map.RefreshAsync();
        Assert.False(view.Map.Data!.Layers[1].Visible);
        host.Close();
    }

    [AvaloniaFact]
    public async Task GridFilter_IsUsedByTheMapAndChart()
    {
        var settings = new ChartSettings();
        var (view, host) = Show(settings);
        Assert.Equal(40, view.ShownRowCount);

        view.AddFilterCondition("state", MapConditionOperator.Equals, "turn");
        Assert.Equal(12, view.ShownRowCount);              // rows 7-9, 17-19, 27-29, 37-39
        Assert.Equal(40, view.RowCount);
        Assert.Equal(1, settings.FilterVersion);

        view.SetMode(ResultMode.Map);
        await view.Map!.RefreshAsync();
        Assert.Equal(12, view.Map.Data!.PointCount);

        view.SetMode(ResultMode.Chart);
        await view.Chart!.RefreshAsync();
        Assert.All(view.Chart.Data!.Series, s => Assert.Equal(12, s.Xs.Length));

        // Quick search on top of the condition, then clear everything.
        view.SetQuickFilter("52.003");
        Assert.True(view.ShownRowCount < 12);
        view.ClearFilter();
        Assert.Equal(40, view.ShownRowCount);
        await view.Chart.RefreshAsync();
        Assert.All(view.Chart.Data!.Series, s => Assert.Equal(40, s.Xs.Length));
        host.Close();
    }

    [AvaloniaFact]
    public void Filter_IsSharedByTheQuerysResultTabs()
    {
        var settings = new ChartSettings();
        var (first, host1) = Show(settings);
        var (second, host2) = Show(settings);
        first.AddFilterCondition("state", MapConditionOperator.Equals, "work");
        Assert.Equal(28, first.ShownRowCount);
        // The other tab re-filters when it is shown again.
        host2.Content = null;
        host2.Content = second;
        Assert.Equal(28, second.ShownRowCount);
        host1.Close();
        host2.Close();
    }

    [AvaloniaFact]
    public void Canvas_FitsAndHitTestsPoints()
    {
        var canvas = new MapCanvas { Width = 800, Height = 500 };
        var host = new Window { Content = canvas, Width = 800, Height = 500 };
        host.Show();
        var data = MapTrackBuilder.Build(new[] { Track() }, new MapOptions());
        canvas.SetData(data, fit: true);
        host.CaptureRenderedFrame();   // the fit is applied when the map is drawn
        Assert.True(canvas.Zoom > 10);
        Assert.Null(canvas.HitTestPoint(new Avalonia.Point(2, 2), 5));
        host.Close();
    }
}
