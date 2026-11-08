using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Tickbird.Core.Data;
using Tickbird.Desktop.Views.Requests;

namespace Tickbird.Desktop.Tests;

public class TagValueIntellisenseTests
{
    private sealed class Loader
    {
        public readonly List<(string Measurement, string Tag)> Calls = new();
        public Task<IReadOnlyList<string>> Load(string measurement, string tag)
        {
            Calls.Add((measurement, tag));
            IReadOnlyList<string> values = tag == "host" ? new[] { "web-01", "web-02", "db-01" } : new[] { "eu", "us" };
            return Task.FromResult(values);
        }
    }

    private static async Task<(Window Host, QueryView View, Loader Loader)> CreateAsync(string text)
    {
        IReadOnlyList<MeasurementSchema> schema = new[]
        {
            new MeasurementSchema("cpu", new[] { new FieldKey("usage_idle", "float") }, new[] { "host", "region" }),
        };
        var loader = new Loader();
        var view = new QueryView { Database = "telegraf", SchemaLoader = () => Task.FromResult(schema), TagValueLoader = loader.Load };
        var host = new Window { Content = view, Width = 900, Height = 600 };
        host.Show();
        await view.InitializeIntellisenseAsync();
        view.EditorText = text;
        view.Editor.Focus();
        view.Editor.CaretOffset = text.Length;
        Pump();
        return (host, view, loader);
    }

    private static void Pump()
    {
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
    }

    private static void Type(QueryView view, string text)
    {
        foreach (var c in text)
        {
            view.Editor.TextArea.PerformTextInput(c.ToString());
            Pump();
        }
    }

    [AvaloniaFact]
    public async Task TypingTagEquals_ListsThatTagsValues_LoadedOnce()
    {
        var (host, view, loader) = await CreateAsync("SELECT * FROM cpu WHERE host =");
        Type(view, " ");
        Assert.True(view.IsShowingTagValues);
        Assert.Equal(new[] { "'db-01'", "'web-01'", "'web-02'" }, view.CompletionTexts);
        Assert.Equal(new[] { ("cpu", "host") }, loader.Calls);

        view.CommitSelectedCompletion();
        Pump();
        Assert.Equal("SELECT * FROM cpu WHERE host = 'db-01'", view.EditorText);

        // Values are kept with the tag: asking again does not reload them.
        Type(view, " AND host = ");
        Assert.True(view.IsShowingTagValues);
        Assert.Single(loader.Calls);
        host.Close();
    }

    [AvaloniaFact]
    public async Task TypingInsideQuotes_FiltersValues()
    {
        var (host, view, _) = await CreateAsync("SELECT * FROM cpu WHERE host = ");
        Type(view, "'web");
        Assert.True(view.IsShowingTagValues);
        Assert.Equal(new[] { "'web-01'", "'web-02'" }, view.CompletionTexts);
        view.CommitSelectedCompletion();
        Pump();
        Assert.Equal("SELECT * FROM cpu WHERE host = 'web-01'", view.EditorText);
        host.Close();
    }

    [AvaloniaFact]
    public async Task PickingATagInWhere_InsertsComparison_AndShowsItsValues()
    {
        var (host, view, _) = await CreateAsync("SELECT * FROM cpu WHERE ");
        Type(view, "reg");
        Assert.Contains("region", view.CompletionTexts);
        view.CommitSelectedCompletion();
        Pump();
        await Task.Delay(50);
        Pump();
        Assert.Equal("SELECT * FROM cpu WHERE region = ", view.EditorText);
        Assert.True(view.IsShowingTagValues);
        Assert.Equal(new[] { "'eu'", "'us'" }, view.CompletionTexts);
        host.Close();
    }

    [AvaloniaFact]
    public async Task PickingATagInGroupBy_InsertsOnlyTheTag()
    {
        var (host, view, loader) = await CreateAsync("SELECT * FROM cpu GROUP BY ");
        Type(view, "hos");
        view.CommitSelectedCompletion();
        Pump();
        Assert.Equal("SELECT * FROM cpu GROUP BY host", view.EditorText);
        Assert.False(view.IsShowingTagValues);
        Assert.Contains(("cpu", "host"), loader.Calls);   // values fetched in the background, ready for later
        host.Close();
    }

    [AvaloniaFact]
    public async Task FieldComparison_DoesNotListValues()
    {
        var (host, view, loader) = await CreateAsync("SELECT * FROM cpu WHERE usage_idle =");
        Type(view, " ");
        Assert.False(view.IsShowingTagValues);
        Assert.Empty(loader.Calls);
        host.Close();
    }
}
