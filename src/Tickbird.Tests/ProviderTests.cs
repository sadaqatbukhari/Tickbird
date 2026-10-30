using Newtonsoft.Json;
using Tickbird.Core.Data;
using Tickbird.Core.Data.Sql;
using Tickbird.Core.Navigation;
using Tickbird.Core.Providers;

namespace Tickbird.Tests;

public class ProviderTests
{
    [Fact]
    public void EveryKind_HasAProfile_WithItsDefaults()
    {
        foreach (var kind in Enum.GetValues<DatabaseKind>())
            Assert.Equal(kind, DatabaseProfiles.Get(kind).Kind);

        Assert.Equal(8086, DatabaseProfiles.Get(DatabaseKind.InfluxDb1).DefaultPort);
        Assert.Equal(8181, DatabaseProfiles.Get(DatabaseKind.InfluxDb3).DefaultPort);
        Assert.Equal(5432, DatabaseProfiles.Get(DatabaseKind.TimescaleDb).DefaultPort);
        Assert.False(DatabaseProfiles.Get(DatabaseKind.InfluxDb1).IsSql);
        Assert.True(DatabaseProfiles.Get(DatabaseKind.InfluxDb3).IsSql);
        Assert.True(DatabaseProfiles.Get(DatabaseKind.TimescaleDb).IsSql);
        Assert.Equal(AuthenticationStyle.Token, DatabaseProfiles.Get(DatabaseKind.InfluxDb3).Authentication);
        Assert.Equal(AuthenticationStyle.UsernamePassword, DatabaseProfiles.Get(DatabaseKind.TimescaleDb).Authentication);
    }

    [Fact]
    public void TheConnectionDialogList_HasAUniqueName_ForEachDatabase()
    {
        var names = DatabaseProfiles.All.Select(p => p.DisplayName).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Contains("TimescaleDB", names);
        Assert.Equal(new[] { "InfluxDB 1.x", "InfluxDB 3.x", "TimescaleDB" }, names);
    }

    [Fact]
    public void ConnectionsSavedBeforeKinds_AreInfluxDb_ByServerVersion()
    {
        var v1 = JsonConvert.DeserializeObject<DatabaseConnection>("{\"Id\":\"1\",\"Name\":\"a\",\"Host\":\"h\",\"Port\":8086}")!;
        var v3 = JsonConvert.DeserializeObject<DatabaseConnection>("{\"Id\":\"2\",\"Name\":\"b\",\"Host\":\"h\",\"Port\":8181,\"ServerVersion\":3}")!;
        Assert.Equal(DatabaseKind.InfluxDb1, v1.Kind);
        Assert.Equal(DatabaseKind.InfluxDb3, v3.Kind);
        Assert.Equal("InfluxDB 3.x", v3.Profile.DisplayName);
    }

    [Fact]
    public void TheKind_IsSaved_AndKeepsTheOldServerVersionRight()
    {
        var timescale = new DatabaseConnection("t", "ts", "h", 5432, "u", "p") { Kind = DatabaseKind.TimescaleDb };
        var copy = JsonConvert.DeserializeObject<DatabaseConnection>(JsonConvert.SerializeObject(timescale))!;
        Assert.Equal(DatabaseKind.TimescaleDb, copy.Kind);

        var influx3 = new DatabaseConnection("i", "i3", "h", 8181, null, null) { Kind = DatabaseKind.InfluxDb3 };
        Assert.Equal(InfluxDbServerVersion.InfluxDb3, influx3.ServerVersion);     // older files and code still read it correctly
        influx3.Kind = DatabaseKind.InfluxDb1;
        Assert.Equal(InfluxDbServerVersion.InfluxDb1, influx3.ServerVersion);
    }

    [Fact]
    public void TheFactory_CreatesTheRightClient()
    {
        Assert.IsType<OfficialInfluxDb1Client>(DatabaseClientFactory.Create(new DatabaseConnection("1", "a", "h", 8086, null, null)));
        Assert.IsType<InfluxDb3Client>(DatabaseClientFactory.Create(new DatabaseConnection("2", "b", "h", 8181, null, null) { Kind = DatabaseKind.InfluxDb3 }));
        Assert.IsType<TimescaleDbClient>(DatabaseClientFactory.Create(new DatabaseConnection("3", "c", "h", 5432, "u", "p") { Kind = DatabaseKind.TimescaleDb }));
    }

    [Fact]
    public void Commands_FollowTheCapabilities_OfTheDatabase()
    {
        var timescale = DatabaseProfiles.Get(DatabaseKind.TimescaleDb);
        var onServer = CommandAvailability.For(NodeKind.Connection, "srv", timescale, false, false);
        Assert.True(onServer.ShowUsers && onServer.ShowPolicies && onServer.ShowStats && onServer.ShowDiagnostics && onServer.ShowQueries && onServer.CreateDatabase);

        var onDatabase = CommandAvailability.For(NodeKind.Database, "telemetry", timescale, false, false);
        Assert.True(onDatabase.ContinuousQueries && onDatabase.Backfill && onDatabase.DropDatabase);
        Assert.False(CommandAvailability.For(NodeKind.Database, "postgres", timescale, false, false).DropDatabase);   // the server's own database

        var onTable = CommandAvailability.For(NodeKind.Measurement, "cpu", timescale, false, false);
        Assert.True(onTable.TagKeys && onTable.TagValues && onTable.FieldKeys && onTable.ShowSeries && onTable.DropSeries && onTable.DropMeasurement);
    }

    [Fact]
    public void InfluxDb3_StillHidesWhatItCannotDo()
    {
        var v3 = DatabaseProfiles.Get(DatabaseKind.InfluxDb3);
        var onServer = CommandAvailability.For(NodeKind.Connection, "srv", v3, false, false);
        Assert.True(onServer.CreateDatabase);
        Assert.False(onServer.ShowUsers || onServer.ShowPolicies || onServer.ShowStats || onServer.ShowDiagnostics || onServer.ShowQueries);
        var onTable = CommandAvailability.For(NodeKind.Measurement, "cpu", v3, false, false);
        Assert.True(onTable.TagKeys && onTable.FieldKeys && onTable.DropMeasurement);
        Assert.False(onTable.ShowSeries || onTable.DropSeries);
        Assert.False(CommandAvailability.For(NodeKind.Database, "_internal", v3, false, false).DropDatabase);
    }

    [Fact]
    public void TheWordsOfTheUi_FollowTheDatabase()
    {
        var timescale = DatabaseProfiles.Get(DatabaseKind.TimescaleDb).Terms;
        Assert.Equal("Table", timescale.Measurement);
        Assert.Equal("Continuous Aggregates", timescale.ContinuousQueries);
        Assert.Equal("Roles", timescale.Users);
        var influx = DatabaseProfiles.Get(DatabaseKind.InfluxDb1).Terms;
        Assert.Equal("Tag Keys", influx.TagKeys);
        Assert.Equal("Continuous Queries", influx.ContinuousQueries);
    }

    [Fact]
    public void AnotherDatabase_CanBeRegistered_WithoutTouchingTheRest()
    {
        var profile = new DatabaseProfile((DatabaseKind)99, "Example DB", 1234, isSql: true, AuthenticationStyle.Token,
            DatabaseCapabilities.TagKeys, DatabaseTerms.Influx, c => new TimescaleDbClient(c));
        DatabaseProfiles.Register(profile);
        try
        {
            Assert.Same(profile, DatabaseProfiles.Get((DatabaseKind)99));
            var connection = new DatabaseConnection("x", "x", "h", 1234, null, null) { Kind = (DatabaseKind)99 };
            Assert.True(CommandAvailability.For(NodeKind.Measurement, "t", connection.Profile, false, false).TagKeys);
            Assert.False(CommandAvailability.For(NodeKind.Measurement, "t", connection.Profile, false, false).FieldKeys);
        }
        finally
        {
            DatabaseProfiles.Unregister((DatabaseKind)99);   // registering is global; put the list back for the other tests
        }
    }
}
