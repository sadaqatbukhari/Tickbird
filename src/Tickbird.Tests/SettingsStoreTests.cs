using Tickbird.Core.Data;
using Tickbird.Core.Settings;

namespace Tickbird.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "tickbird-tests-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(dir, "nested", "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults() =>
        Assert.Empty(new SettingsStore(FilePath).Load().Connections);

    [Fact]
    public void SaveThenLoad_RoundTripsConnections()
    {
        var store = new SettingsStore(FilePath);
        var settings = new TickbirdSettings { AllowUntrustedSsl = true };
        settings.Connections.Add(new DatabaseConnection("a", "Prod", "h", 8086, "u", "p", true, null, InfluxDbServerVersion.InfluxDb3));
        store.Save(settings);

        var loaded = store.Load();
        Assert.True(loaded.AllowUntrustedSsl);
        var c = Assert.Single(loaded.Connections);
        Assert.Equal("a", c.Id);
        Assert.Equal(InfluxDbServerVersion.InfluxDb3, c.ServerVersion);
    }

    [Fact]
    public void CorruptFile_FallsBackToLastKnownGoodBackup()
    {
        var store = new SettingsStore(FilePath);
        var good = new TickbirdSettings();
        good.Connections.Add(new DatabaseConnection("keep", "Keep", "h", 8086, "u", "p"));
        store.Save(good);
        store.Save(good); // second save creates the .bak
        File.WriteAllText(FilePath, "{ corrupt");

        Assert.Equal("keep", Assert.Single(store.Load().Connections).Id);
    }

    [Fact]
    public void CorruptFileWithoutBackup_ReturnsDefaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, "garbage");
        Assert.Empty(new SettingsStore(FilePath).Load().Connections);
    }

    [Fact]
    public void Save_LeavesNoTempFile()
    {
        new SettingsStore(FilePath).Save(new TickbirdSettings());
        Assert.False(File.Exists(FilePath + ".tmp"));
    }
}
