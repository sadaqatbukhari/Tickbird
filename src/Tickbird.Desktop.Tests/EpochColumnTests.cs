using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Tickbird.Core.Data;
using Tickbird.Core.Query;
using Tickbird.Core.Settings;
using Tickbird.Desktop.Services;
using Tickbird.Desktop.Views.Requests;

namespace Tickbird.Desktop.Tests;

public sealed class EpochColumnTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "tickbird-epoch-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    private (ResultsView View, Window Host) Show(ChartSettings? settings = null)
    {
        var app = new TickbirdApp(new SettingsStore(Path.Combine(dir, "settings.json")));
        app.Settings.DateFormat = TickbirdSettings.DateFormatIso;
        app.Settings.TimeFormat = TickbirdSettings.TimeFormat24Hour;
        TickbirdApp.Current = app;
        var view = new ResultsView { ChartSettings = settings ?? new ChartSettings() };
        var host = new Window { Content = view, Width = 1000, Height = 500 };
        host.Show();
        view.Show(Series());
        Pump();
        return (view, host);
    }

    private static ResultSeries Series() =>
        new("events", new List<string> { "time", "ts_ms", "name" }, new Dictionary<string, string>(),
            new List<IList<object>>
            {
                new List<object> { "2026-10-21T07:00:00Z", 1_792_567_815_000L, "start" },
                new List<object> { "2026-10-21T07:01:00Z", 1_792_567_875_000L, "stop" },
            });

    private static void Pump()
    {
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
    }

    private static string? Cell(ResultsView view, int row, int column) => view.CellText(row, column);

    private static string HeaderOf(ResultsView view, int column) => ((TextBlock)view.ResultsGrid.Columns[column].Header!).Text!;

    [AvaloniaFact]
    public void ConvertingAColumn_ShowsDates_AndTheHeaderSaysUtc()
    {
        var (view, host) = Show();
        Assert.Equal("1792567815000", Cell(view, 0, 2));
        Assert.Equal("ts_ms", HeaderOf(view, 2));

        view.SetEpochColumn(1, EpochUnit.Auto);
        Pump();

        Assert.Equal("2026-10-21 07:30:15.000", Cell(view, 0, 2));
        Assert.Equal("2026-10-21 07:31:15.000", Cell(view, 1, 2));
        Assert.Equal("ts_ms (UTC)", HeaderOf(view, 2));
        Assert.Equal("start", Cell(view, 0, 3));                 // other columns are untouched
        Assert.Equal(EpochUnit.Auto, view.EpochUnitOfColumn("ts_ms"));
        host.Close();
    }

    [AvaloniaFact]
    public void ShowingAsNumberAgain_RestoresTheValue()
    {
        var (view, host) = Show();
        view.SetEpochColumn(1, EpochUnit.Milliseconds);
        view.SetEpochColumn(1, null);
        Pump();

        Assert.Equal("1792567815000", Cell(view, 0, 2));
        Assert.Equal("ts_ms", HeaderOf(view, 2));
        Assert.Null(view.EpochUnitOfColumn("ts_ms"));
        host.Close();
    }

    [AvaloniaFact]
    public void AWrongUnit_GivesAnotherDate_ButNeverThrows()
    {
        var (view, host) = Show();
        view.SetEpochColumn(1, EpochUnit.Seconds);     // the numbers are milliseconds; as seconds they are past year 9999
        Pump();
        Assert.Equal("1792567815000", Cell(view, 0, 2));
        view.SetEpochColumn(1, EpochUnit.Nanoseconds);
        Pump();
        Assert.StartsWith("1970-01-01 00:29:52", Cell(view, 0, 2));
        host.Close();
    }

    [AvaloniaFact]
    public void TheTimeColumn_CannotBeConverted()
    {
        var (view, host) = Show();
        view.SetEpochColumn(0, EpochUnit.Seconds);
        Assert.Null(view.EpochUnitOfColumn("time"));
        host.Close();
    }

    [AvaloniaFact]
    public void TheChoice_SurvivesRunningTheQueryAgain()
    {
        var settings = new ChartSettings();
        var (view, host) = Show(settings);
        view.SetEpochColumn(1, EpochUnit.Milliseconds);

        view.Show(Series());                              // a re-run shows a new result in the same view
        Pump();

        Assert.Equal("2026-10-21 07:30:15.000", Cell(view, 0, 2));
        Assert.Equal("ts_ms (UTC)", HeaderOf(view, 2));
        host.Close();
    }

    [AvaloniaFact]
    public void TheFilter_SeesTheConvertedText()
    {
        var (view, host) = Show();
        view.SetEpochColumn(1, EpochUnit.Milliseconds);
        view.SetQuickFilter("07:31:15");
        Pump();
        Assert.Equal(1, view.ShownRowCount);
        host.Close();
    }
}
