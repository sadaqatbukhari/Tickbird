using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Controls;
using Avalonia.Threading;
using Tickbird.Core.Data;
using Tickbird.Desktop.Views.Requests;

namespace Tickbird.Desktop.Tests;

public class QueryIntellisenseTests
{
    private static async Task<(Window Host, QueryView View)> CreateAsync(string text)
    {
        IReadOnlyList<MeasurementSchema> schema = new[]
        {
            new MeasurementSchema("cpu", new[] { new FieldKey("usage_idle", "float") }, new[] { "host" }),
            new MeasurementSchema("mem", new[] { new FieldKey("used_percent", "float") }, new[] { "host" }),
        };
        var view = new QueryView { Database = "telegraf", SchemaLoader = () => Task.FromResult(schema) };
        var host = new Window { Content = view, Width = 900, Height = 600 };
        host.Show();
        await view.InitializeIntellisenseAsync();
        view.EditorText = text;
        view.Editor.Focus();
        view.Editor.CaretOffset = text.Length;
        Dispatcher.UIThread.RunJobs();
        return (host, view);
    }

    private static void Type(QueryView view, string text)
    {
        foreach (var c in text)
        {
            view.Editor.TextArea.PerformTextInput(c.ToString());
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task CtrlSpace_OpensList_WithFromMeasurementFieldsFirst()
    {
        var (host, view) = await CreateAsync("SELECT * FROM mem WHERE ");
        view.Editor.TextArea.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space, KeyModifiers = KeyModifiers.Control });
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.IsCompletionOpen);
        Assert.Equal(new[] { "used_percent", "host" }, view.CompletionTexts.Take(2));
        host.Close();
    }

    [AvaloniaFact]
    public async Task TypingAWord_OpensFilteredList()
    {
        var (host, view) = await CreateAsync("SELECT ");
        Type(view, "usa");
        Assert.True(view.IsCompletionOpen);
        Assert.Contains("usage_idle", view.CompletionTexts);
        Assert.DoesNotContain("used_percent", view.CompletionTexts);
        host.Close();
    }

    [AvaloniaFact]
    public async Task SpaceAfterFrom_OffersMeasurements()
    {
        var (host, view) = await CreateAsync("SELECT * FROM");
        Type(view, " ");
        Assert.True(view.IsCompletionOpen);
        Assert.Equal(new[] { "cpu", "mem" }, view.CompletionTexts.Take(2));
        host.Close();
    }

    [AvaloniaFact]
    public async Task NoList_InsideComments()
    {
        var (host, view) = await CreateAsync("-- ");
        Type(view, "us");
        Assert.False(view.IsCompletionOpen);
        host.Close();
    }

    [AvaloniaFact]
    public async Task SchemaCounts_AreShownInTheStatusLine()
    {
        var (host, view) = await CreateAsync("");
        Assert.Equal(2, view.Completion.Measurements.Count);
        Assert.Equal(2, view.Completion.FieldCount);
        host.Close();
    }
}
