using Tickbird.Core.Query;
using Tickbird.Core.History;

namespace Tickbird.Tests;

public sealed class QueryHistoryCaptionTests
{
    private static QueryHistoryEntry Entry(string query) => new()
    {
        RunAtUtc = DateTime.UtcNow, ConnectionId = "c1", ConnectionName = "C1", Database = "telemetry", Query = query
    };

    [Fact]
    public void Caption_IsSearchable_AndTrimmed()
    {
        var history = new QueryHistory(null);
        history.Add(Entry("SELECT * FROM cpu"));
        history.Add(Entry("SELECT * FROM mem"));
        var id = history.Entries.First(e => e.Query.Contains("cpu")).Id;

        Assert.True(history.SetCaption(id, "  Weekly load report "));

        Assert.Equal("Weekly load report", history.Entries.First(e => e.Id == id).Caption);
        Assert.Single(history.Search("weekly"));
        Assert.Equal(1, history.CaptionedCount);
    }

    [Fact]
    public void Caption_SurvivesRunningTheQueryAgain_AndCanBeCleared()
    {
        var history = new QueryHistory(null);
        history.Add(Entry("SELECT 1"));
        history.SetCaption(history.Entries[0].Id, "One");

        history.Add(Entry("SELECT 1"));

        Assert.Single(history.Entries);
        Assert.Equal("One", history.Entries[0].Caption);
        history.SetCaption(history.Entries[0].Id, " ");
        Assert.Null(history.Entries[0].Caption);
    }
}

public sealed class QueryHistoryTagsAndSnippetsTests
{
    [Fact]
    public void Tags_AreCleaned_Searchable_AndSurviveRerun()
    {
        var history = new QueryHistory(null);
        history.Add(new QueryHistoryEntry { RunAtUtc = DateTime.UtcNow, ConnectionId = "c", Query = "SELECT 1" });
        history.SetTags(new[] { history.Entries[0].Id }, new[] { " Prod ", "prod", "", "cpu" });

        Assert.Equal(new[] { "cpu", "Prod" }, history.Entries[0].Tags);
        Assert.Single(history.Search("prod"));

        history.Add(new QueryHistoryEntry { RunAtUtc = DateTime.UtcNow, ConnectionId = "c", Query = "SELECT 1" });
        Assert.Equal(new[] { "cpu", "Prod" }, history.Entries[0].Tags);
        Assert.Equal(new[] { "cpu", "Prod" }, history.AllTags);
    }

    [Fact]
    public void Parameters_AreFound_AndReplaced()
    {
        var q = "SELECT * FROM {{m}} WHERE host = '{{ host }}' AND x > {{m}}";
        Assert.Equal(new[] { "m", "host" }, QueryParameters.Find(q));
        var result = QueryParameters.Apply(q, new Dictionary<string, string> { ["m"] = "cpu", ["host"] = "a" });
        Assert.Equal("SELECT * FROM cpu WHERE host = 'a' AND x > cpu", result);
    }

    [Fact]
    public void Snippets_SaveReplaceRenameRemove()
    {
        var file = Path.Combine(Path.GetTempPath(), "tb-snip-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var snippets = new Tickbird.Core.Query.QuerySnippets(file);
            var a = snippets.Save("Load", "SELECT 1");
            snippets.Save("load", "SELECT 2");
            snippets.Save("Other", "SELECT 3");
            Assert.Equal(2, snippets.Items.Count);
            Assert.False(snippets.Rename(a.Id, "OTHER"));
            Assert.True(snippets.Rename(a.Id, "Zed"));

            var reloaded = new Tickbird.Core.Query.QuerySnippets(file);
            Assert.Equal(new[] { "Other", "Zed" }, reloaded.Items.Select(s => s.Name));
            Assert.Equal("SELECT 2", reloaded.Items.Last().Query);
            Assert.True(reloaded.Remove(a.Id));
            Assert.Single(reloaded.Items);
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }
}
