using Tickbird.Core.Data;
using Tickbird.Core.Providers;
using Tickbird.Core.Navigation;
using Tickbird.Core.Settings;
using Newtonsoft.Json;

namespace Tickbird.Tests;

public sealed class ConnectionGroupTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "influxexplorer-groups-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    private static DatabaseConnection Conn(string id, string? group) =>
        new(id, id, "h", 8086, "u", "p") { Group = group };

    [Fact]
    public void AllGroups_CombinesSavedAndConnectionGroups_DistinctAndSorted()
    {
        var s = new TickbirdSettings { Groups = { "Staging", "prod" } };
        s.Connections.Add(Conn("a", "PROD"));
        s.Connections.Add(Conn("b", " DEV "));
        s.Connections.Add(Conn("c", null));
        s.Connections.Add(Conn("d", "  "));
        Assert.Equal(new[] { "DEV", "prod", "Staging" }, s.AllGroups());
    }

    [Fact]
    public void EnsureGroup_ReusesExistingCasing_AndTreatsBlankAsNoGroup()
    {
        var s = new TickbirdSettings { Groups = { "PROD" } };
        Assert.Equal("PROD", s.EnsureGroup(" prod "));
        Assert.Single(s.Groups);
        Assert.Null(s.EnsureGroup("   "));
        Assert.Equal("DEV", s.EnsureGroup("DEV"));
        Assert.Equal(new[] { "PROD", "DEV" }, s.Groups);
    }

    [Fact]
    public void EnsureGroup_SavesAGroupThatOnlyExistedOnAConnection()
    {
        var s = new TickbirdSettings();
        s.Connections.Add(Conn("a", "QA"));
        Assert.Equal("QA", s.EnsureGroup("qa"));
        Assert.Equal(new[] { "QA" }, s.Groups);
    }

    [Fact]
    public void RenameGroup_MovesItsConnections()
    {
        var s = new TickbirdSettings { Groups = { "DEV" } };
        s.Connections.Add(Conn("a", "dev"));
        s.Connections.Add(Conn("b", "PROD"));
        s.RenameGroup("DEV", "Development");
        Assert.Equal("Development", s.Connections[0].Group);
        Assert.Equal("PROD", s.Connections[1].Group);
        Assert.Equal(new[] { "Development", "PROD" }, s.AllGroups());
    }

    [Fact]
    public void RemoveGroup_KeepsConnections_AsUngrouped()
    {
        var s = new TickbirdSettings { Groups = { "DEV" } };
        s.Connections.Add(Conn("a", "DEV"));
        s.RemoveGroup("dev");
        Assert.Single(s.Connections);
        Assert.Null(s.Connections[0].Group);
        Assert.Empty(s.AllGroups());
    }

    [Fact]
    public void CountInGroup_IgnoresCase()
    {
        var s = new TickbirdSettings();
        s.Connections.Add(Conn("a", "PROD"));
        s.Connections.Add(Conn("b", "prod"));
        s.Connections.Add(Conn("c", null));
        Assert.Equal(2, s.CountInGroup("Prod"));
    }

    [Fact]
    public void Groups_RoundTripThroughSettingsStore()
    {
        var store = new SettingsStore(Path.Combine(dir, "settings.json"));
        var s = new TickbirdSettings { Groups = { "Staging" } };
        s.Connections.Add(Conn("a", "PROD"));
        store.Save(s);
        var loaded = store.Load();
        Assert.Equal("PROD", loaded.Connections[0].Group);
        Assert.Equal(new[] { "PROD", "Staging" }, loaded.AllGroups());
    }

    [Fact]
    public void OlderSettingsWithoutGroups_LoadAsUngrouped()
    {
        const string json = "{\"Connections\":[{\"Id\":\"a\",\"Name\":\"Old\",\"Host\":\"h\",\"Port\":8086}]}";
        var loaded = JsonConvert.DeserializeObject<TickbirdSettings>(json)!;
        Assert.Null(loaded.Connections[0].Group);
        Assert.Empty(loaded.AllGroups());
        Assert.Null(loaded.EnsureGroup(loaded.Connections[0].Group));
    }

    [Fact]
    public void MergeGroups_AddsMissingOnly()
    {
        var s = new TickbirdSettings { Groups = { "PROD" } };
        s.MergeGroups(new[] { "prod", "DEV", "" });
        s.MergeGroups(null);
        Assert.Equal(new[] { "PROD", "DEV" }, s.Groups);
    }

    [Theory]
    [InlineData(null, "Group name cannot be blank.")]
    [InlineData("  ", "Group name cannot be blank.")]
    [InlineData("prod", "A group named 'prod' already exists.")]
    [InlineData("Staging", null)]
    public void Validation_Group(string? name, string? expected) =>
        Assert.Equal(expected, Validation.Group(name, new[] { "PROD", "DEV" }));

    [Fact]
    public void Validation_Group_AllowsRenamingToSameNameWithDifferentCase() =>
        Assert.Null(Validation.Group("Prod", new[] { "PROD", "DEV" }, current: "PROD"));

    [Fact]
    public void GroupFolder_EnablesNoServerCommands()
    {
        var c = CommandAvailability.For(NodeKind.Group, "PROD", (DatabaseProfile?)null, false, false);
        Assert.False(c.Disconnect || c.Refresh || c.NewQuery || c.CreateDatabase || c.ShowUsers || c.DropDatabase);
    }
}
