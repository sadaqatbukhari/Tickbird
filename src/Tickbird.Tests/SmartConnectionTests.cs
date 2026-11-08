using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using Npgsql;
using Tickbird.Core.Data;
using Tickbird.Core.Providers;

namespace Tickbird.Tests;

/// <summary>Design A: a pasted connection string fills the form.</summary>
public class ConnectionUrlTests
{
    [Fact]
    public void ATimescaleUrl_IsReadInFull()
    {
        var parsed = ConnectionUrl.TryParse("postgresql://app:pw@db.example.com:5433/metrics?sslmode=require")!;
        Assert.Equal(DatabaseKind.TimescaleDb, parsed.Kind);
        Assert.Equal("db.example.com", parsed.Host);
        Assert.Equal(5433, parsed.Port);
        Assert.Equal("app", parsed.Username);
        Assert.Equal("pw", parsed.Password);
        Assert.Equal("metrics", parsed.Database);
        Assert.True(parsed.UseSsl);
        Assert.Null(parsed.Token);
    }

    [Fact]
    public void PercentEncodedParts_AreDecoded()
    {
        var parsed = ConnectionUrl.TryParse("postgresql://my%20user:p%40ss%3Aw%2Frd@h/my%20db")!;
        Assert.Equal("my user", parsed.Username);
        Assert.Equal("p@ss:w/rd", parsed.Password);
        Assert.Equal("my db", parsed.Database);
    }

    [Theory]
    [InlineData("postgres://localhost", DatabaseKind.TimescaleDb, 5432)]
    [InlineData("timescaledb://localhost", DatabaseKind.TimescaleDb, 5432)]
    [InlineData("crate://localhost", DatabaseKind.CrateDb, 5432)]
    [InlineData("cratedb://localhost:5434", DatabaseKind.CrateDb, 5434)]
    [InlineData("http://localhost", DatabaseKind.InfluxDb1, 8086)]
    [InlineData("https://localhost", DatabaseKind.InfluxDb1, 8086)]
    [InlineData("http://localhost:8086", DatabaseKind.InfluxDb1, 8086)]
    [InlineData("influxdb://localhost", DatabaseKind.InfluxDb1, 8086)]
    [InlineData("influxdb3://localhost", DatabaseKind.InfluxDb3, 8181)]
    [InlineData("http://localhost:8181", DatabaseKind.InfluxDb3, 8181)]
    [InlineData("https://localhost:8181", DatabaseKind.InfluxDb3, 8181)]
    [InlineData("http://localhost:9999", DatabaseKind.InfluxDb1, 9999)]
    public void TheScheme_AndThePort_ChooseTheDatabase(string url, DatabaseKind kind, int port)
    {
        var parsed = ConnectionUrl.TryParse(url)!;
        Assert.Equal(kind, parsed.Kind);
        Assert.Equal(port, parsed.Port);
    }

    [Fact]
    public void ACrateUrl_HasNoDatabaseAndNoPassword()
    {
        var parsed = ConnectionUrl.TryParse("crate://crate@localhost:5434")!;
        Assert.Equal("crate", parsed.Username);
        Assert.Null(parsed.Password);
        Assert.Null(parsed.Database);
        Assert.False(parsed.UseSsl);
    }

    [Fact]
    public void AnInfluxDb3Url_CarriesATokenNotAUser()
    {
        var parsed = ConnectionUrl.TryParse("https://influx.example.com:8181/telemetry?token=abc123")!;
        Assert.Equal(DatabaseKind.InfluxDb3, parsed.Kind);
        Assert.Equal("abc123", parsed.Token);
        Assert.Null(parsed.Username);
        Assert.Null(parsed.Password);
        Assert.Equal("telemetry", parsed.Database);
        Assert.True(parsed.UseSsl);       // https
    }

    [Fact]
    public void AnInfluxDb1Url_CarriesAUserAndPassword()
    {
        var parsed = ConnectionUrl.TryParse("http://u:p@h:8086/db")!;
        Assert.Equal(DatabaseKind.InfluxDb1, parsed.Kind);
        Assert.Equal("u", parsed.Username);
        Assert.Equal("p", parsed.Password);
        Assert.Equal("db", parsed.Database);
        Assert.Null(parsed.Token);
        Assert.False(parsed.UseSsl);
    }

    [Theory]
    [InlineData("postgresql://h?sslmode=require", true)]
    [InlineData("postgresql://h?sslmode=verify-ca", true)]
    [InlineData("postgresql://h?sslmode=verify-full", true)]
    [InlineData("postgresql://h?sslmode=disable", false)]
    [InlineData("postgresql://h?sslmode=prefer", false)]
    [InlineData("postgresql://h?ssl=true", true)]
    [InlineData("postgresql://h?tls=1", true)]
    [InlineData("postgresql://h?ssl=false", false)]
    [InlineData("postgresql://h", false)]
    [InlineData("https://h", true)]
    public void Ssl_FollowsTheStringsWords(string url, bool ssl) => Assert.Equal(ssl, ConnectionUrl.TryParse(url)!.UseSsl);

    [Theory]
    [InlineData("postgresql://h?database=one", "one")]
    [InlineData("postgresql://h?db=two", "two")]
    [InlineData("postgresql://h/three?db=ignored", "three")]
    public void TheDatabase_CanBeInThePathOrTheQuery(string url, string database) => Assert.Equal(database, ConnectionUrl.TryParse(url)!.Database);

    [Fact]
    public void AnIpv6Host_LosesItsBrackets()
    {
        var parsed = ConnectionUrl.TryParse("postgresql://[::1]:5433/db")!;
        Assert.Equal("::1", parsed.Host);
        Assert.Equal(5433, parsed.Port);
    }

    [Fact]
    public void SurroundingSpaces_AreIgnored() => Assert.Equal("h", ConnectionUrl.TryParse("  postgresql://h  ")!.Host);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("localhost:5432")]
    [InlineData("localhost")]
    [InlineData("ftp://example.com")]
    [InlineData("mysql://root@localhost/db")]
    [InlineData("postgresql://")]
    [InlineData("://host")]
    [InlineData("postgresql:/host")]
    public void WhatIsNotAKnownConnectionString_IsNull(string? text) => Assert.Null(ConnectionUrl.TryParse(text));

    [Theory]
    [InlineData("postgresql://h", true)]
    [InlineData("whatever://h", true)]
    [InlineData("localhost:5432", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ALookalike_IsRecognisedBeforeItIsParsed(string? text, bool expected) => Assert.Equal(expected, ConnectionUrl.LooksLikeUrl(text));

    [Fact]
    public void ANewDatabase_BringsItsOwnScheme()
    {
        var profile = new DatabaseProfile((DatabaseKind)98, "Example", 4321, isSql: true, AuthenticationStyle.UsernamePassword,
            DatabaseCapabilities.TagKeys, DatabaseTerms.Influx, c => new CrateDbClient(c)) { UrlSchemes = new[] { "example" } };
        DatabaseProfiles.Register(profile);
        try
        {
            var parsed = ConnectionUrl.TryParse("example://u@h")!;
            Assert.Equal((DatabaseKind)98, parsed.Kind);
            Assert.Equal(4321, parsed.Port);
        }
        finally { DatabaseProfiles.Unregister((DatabaseKind)98); }
    }

    [Fact]
    public void EveryProfileThatCanBePasted_HasAtLeastOneScheme()
    {
        // Only the real databases: other tests register a fake one for a moment, and the registry is shared.
        var real = Enum.GetValues<DatabaseKind>().Select(DatabaseProfiles.Get).ToList();
        foreach (var profile in real) Assert.NotEmpty(profile.UrlSchemes);
        var schemes = real.SelectMany(p => p.UrlSchemes).ToList();
        Assert.Equal(new[] { "http", "https" }, schemes.GroupBy(s => s).Where(g => g.Count() > 1).Select(g => g.Key).OrderBy(s => s));
    }
}

/// <summary>Design B: what the form says while it is typed.</summary>
public class ConnectionAdvisorTests
{
    private static DatabaseConnection Connection(DatabaseKind kind, string host = "localhost", ushort port = 0, string? user = null, string? token = null, string name = "c") =>
        new("1", name, host, port == 0 ? DatabaseProfiles.Get(kind).DefaultPort : port, user, null) { Kind = kind, Token = token };

    private static IEnumerable<string> Messages(DatabaseConnection c, AdviceSeverity severity) =>
        ConnectionAdvisor.Advise(c).Where(a => a.Severity == severity).Select(a => a.Message);

    [Fact]
    public void ACleanConnection_HasNothingToSay()
    {
        Assert.Empty(ConnectionAdvisor.Advise(Connection(DatabaseKind.InfluxDb1, "influx.example.com")));
        Assert.Empty(ConnectionAdvisor.Advise(Connection(DatabaseKind.TimescaleDb, "db.example.com")));
    }

    [Theory]
    [InlineData("http://localhost", "Remove \"http://\"")]
    [InlineData("https://db.example.com", "Remove \"https://\"")]
    [InlineData("postgresql://db", "Remove \"postgresql://\"")]
    [InlineData("localhost/metrics", "Remove the path")]
    [InlineData("db:5433", "port in the Port box")]
    [InlineData("local host", "contains a space")]
    [InlineData("0.0.0.0", "0.0.0.0 is the address a server listens on")]
    public void ATypicalMistakeInTheHost_IsExplained(string host, string expected) =>
        Assert.Contains(Messages(Connection(DatabaseKind.TimescaleDb, host, 5433), AdviceSeverity.Warning), m => m.Contains(expected));

    [Fact]
    public void AnIpv6Address_IsNotMistakenForAHostAndPort()
    {
        Assert.Empty(Messages(Connection(DatabaseKind.TimescaleDb, "::1", 5433), AdviceSeverity.Warning));
        Assert.Empty(Messages(Connection(DatabaseKind.TimescaleDb, "[::1]", 5433), AdviceSeverity.Warning));
    }

    [Fact]
    public void TheHttpPortOfCrateDb_IsAnError_ThatComesFirst()
    {
        var notes = ConnectionAdvisor.Advise(Connection(DatabaseKind.CrateDb, "http://localhost", 4200));
        Assert.Equal(AdviceSeverity.Error, notes[0].Severity);
        Assert.Contains("HTTP port", notes[0].Message);
        Assert.Contains(notes, n => n.Severity == AdviceSeverity.Warning);
        Assert.Contains("HTTP port", ConnectionAdvisor.FirstError(Connection(DatabaseKind.CrateDb, "localhost", 4200)));
    }

    [Fact]
    public void ABlankNameOrHost_IsAnError()
    {
        Assert.Contains("Connection name cannot be blank.", Messages(Connection(DatabaseKind.InfluxDb1, name: " "), AdviceSeverity.Error));
        Assert.Contains("Host cannot be blank.", Messages(Connection(DatabaseKind.InfluxDb1, host: ""), AdviceSeverity.Error));
        Assert.Null(ConnectionAdvisor.FirstError(Connection(DatabaseKind.InfluxDb1)));
    }

    [Fact]
    public void Warnings_DoNotBlockSaving()
    {
        var connection = Connection(DatabaseKind.TimescaleDb, "http://localhost", 5433);
        Assert.NotEmpty(Messages(connection, AdviceSeverity.Warning));
        Assert.Null(ConnectionAdvisor.FirstError(connection));
    }

    [Fact]
    public void InfluxDb3_WithoutAToken_IsWarnedAbout()
    {
        Assert.Contains(Messages(Connection(DatabaseKind.InfluxDb3, "h"), AdviceSeverity.Warning), m => m.Contains("needs an API token"));
        Assert.Empty(Messages(Connection(DatabaseKind.InfluxDb3, "h", token: "t"), AdviceSeverity.Warning));
        Assert.Empty(Messages(Connection(DatabaseKind.TimescaleDb, "h"), AdviceSeverity.Warning));
    }

    [Theory]
    [InlineData(DatabaseKind.TimescaleDb, "localhost", 5432, true)]
    [InlineData(DatabaseKind.CrateDb, "127.0.0.1", 5432, true)]
    [InlineData(DatabaseKind.TimescaleDb, "localhost", 5433, false)]
    [InlineData(DatabaseKind.TimescaleDb, "db.example.com", 5432, false)]
    [InlineData(DatabaseKind.InfluxDb1, "localhost", 5432, false)]
    public void ALocalPostgres_IsMentioned_OnlyWhereItCouldClash(DatabaseKind kind, string host, int port, bool mentioned) =>
        Assert.Equal(mentioned, Messages(Connection(kind, host, (ushort)port), AdviceSeverity.Info).Any(m => m.Contains("5432")));

    [Fact]
    public void Notes_AreOrdered_ErrorsThenWarningsThenInfo()
    {
        var notes = ConnectionAdvisor.Advise(Connection(DatabaseKind.CrateDb, "http://localhost", 4200, name: ""));
        Assert.Equal(notes.OrderByDescending(n => n.Severity).ToList(), notes);
    }

    [Theory]
    [InlineData("http://db.example.com:8086/path?x=1", "db.example.com")]
    [InlineData("  localhost  ", "localhost")]
    [InlineData("user@host", "host")]
    [InlineData("db:5433", "db")]
    [InlineData("db", "db")]
    [InlineData("[::1]:5432", "::1")]
    [InlineData("::1", "::1")]
    [InlineData("postgresql://u:p@db.example.com:5432/x", "db.example.com")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void TheHost_IsCleaned(string? typed, string expected) => Assert.Equal(expected, ConnectionAdvisor.CleanHost(typed));

    [Theory]
    [InlineData("db:5433", 5433)]
    [InlineData("http://db:8181/", 8181)]
    [InlineData("db", null)]
    [InlineData("db:abc", null)]
    [InlineData("db:0", null)]
    [InlineData("::1", null)]
    [InlineData(null, null)]
    public void ThePortAfterTheHost_IsFound(string? typed, int? expected) =>
        Assert.Equal(expected == null ? null : (ushort)expected, ConnectionAdvisor.PortInHost(typed));

    [Theory]
    [InlineData(DatabaseKind.CrateDb, "localhost", 5432, "crate", "crate@localhost")]
    [InlineData(DatabaseKind.CrateDb, "localhost", 5434, "crate", "crate@localhost:5434")]
    [InlineData(DatabaseKind.TimescaleDb, "db.example.com", 5432, null, "db.example.com")]
    [InlineData(DatabaseKind.TimescaleDb, "http://db.example.com:5432/", 5433, "app", "app@db.example.com:5433")]
    [InlineData(DatabaseKind.InfluxDb1, "influx", 8086, "admin", "admin@influx")]
    [InlineData(DatabaseKind.InfluxDb3, "influx", 8181, "ignored", "influx")]      // a token login has no user
    [InlineData(DatabaseKind.CrateDb, "", 5432, "crate", "CrateDB")]
    public void ANameIsSuggested_FromTheUserAndTheServer(DatabaseKind kind, string host, int port, string? user, string expected) =>
        Assert.Equal(expected, ConnectionAdvisor.SuggestName(Connection(kind, host, (ushort)port, user)));
}

/// <summary>Design C: a test that says which step failed and what to try.</summary>
public class ConnectionDiagnosticsTests
{
    private static DatabaseConnection Crate(string host = "localhost", ushort port = 5434, string? user = "crate", string? database = null) =>
        new("1", "c", host, port, user, "", false, database) { Kind = DatabaseKind.CrateDb };

    private static Func<string, CancellationToken, Task> Resolves => (_, _) => Task.CompletedTask;
    private static Func<string, int, CancellationToken, Task> Connects => (_, _, _) => Task.CompletedTask;

    private static ConnectionDiagnostics Healthy(FakeSqlRunner runner, Func<string, CancellationToken, Task>? resolve = null) =>
        new(resolve ?? Resolves, Connects, c => new CrateDbClient(c, runner));

    [Fact]
    public async Task AWorkingConnection_PassesEveryStep_AndReportsWhatItFound()
    {
        var runner = new FakeSqlRunner()
            .Answer("sys.cluster", new[] { "name" }, new object[] { "prod" })
            .Answer("version['number']", new[] { "v" }, new object[] { "5.9.2" });
        var report = await Healthy(runner).RunAsync(Crate());

        Assert.True(report.Success);
        Assert.Null(report.Failed);
        Assert.Equal(new[] { "Host found", "Port open", "Sign in" }, report.Steps.Select(s => s.Name));
        Assert.All(report.Steps, s => Assert.Equal(StepStatus.Ok, s.Status));
        Assert.Equal(new[] { "crate" }, report.Databases);
        Assert.Equal("CrateDB 5.9.2", report.Version);
        Assert.Equal("Found 1 database", report.Steps[2].Detail);
        Assert.Null(report.Hint);
    }

    [Fact]
    public async Task AnIpAddress_NeedsNoNameLookup()
    {
        var looked = false;
        var report = await Healthy(new FakeSqlRunner(), (_, _) => { looked = true; return Task.CompletedTask; }).RunAsync(Crate("127.0.0.1"));
        Assert.False(looked);
        Assert.Equal("An IP address", report.Steps[0].Detail);
        Assert.True(report.Success);
    }

    [Fact]
    public async Task TheStepsAreGivenTheCleanedHostAndThePort()
    {
        string? host = null; int port = 0;
        var diagnostics = new ConnectionDiagnostics(Resolves, (h, p, _) => { host = h; port = p; return Task.CompletedTask; }, c => new CrateDbClient(c, new FakeSqlRunner()));
        await diagnostics.RunAsync(Crate("http://crate.example.com:4200/", 5434));
        Assert.Equal("crate.example.com", host);
        Assert.Equal(5434, port);
    }

    [Fact]
    public async Task AnUnknownHost_StopsAtTheFirstStep_AndSaysSo()
    {
        var report = await Healthy(new FakeSqlRunner(), (_, _) => throw new SocketException((int)SocketError.HostNotFound)).RunAsync(Crate("no-such-host"));
        Assert.False(report.Success);
        Assert.Equal("Host found", report.Failed!.Name);
        Assert.Contains("No server was found for \"no-such-host\"", report.Failed.Detail);
        Assert.Contains("typing mistakes", report.Hint);
        Assert.Equal(new[] { StepStatus.Failed, StepStatus.Skipped, StepStatus.Skipped }, report.Steps.Select(s => s.Status));
        Assert.Empty(report.Databases);
    }

    [Fact]
    public async Task ARefusedPort_SaysNothingIsListening_AndNamesTheDefaultPort()
    {
        var diagnostics = new ConnectionDiagnostics(Resolves, (_, _, _) => throw new SocketException((int)SocketError.ConnectionRefused), c => new CrateDbClient(c, new FakeSqlRunner()));
        var report = await diagnostics.RunAsync(Crate(port: 5999));
        Assert.Equal("Port open", report.Failed!.Name);
        Assert.Equal("Nothing is listening on localhost:5999.", report.Failed.Detail);
        Assert.Contains("CrateDB uses port 5432 by default", report.Hint);
        Assert.Equal(StepStatus.Skipped, report.Steps[2].Status);
    }

    [Fact]
    public async Task ASilentServer_TimesOut_WithAHintAboutFirewalls()
    {
        var diagnostics = new ConnectionDiagnostics(Resolves, async (_, _, token) => await Task.Delay(Timeout.Infinite, token), c => new CrateDbClient(c, new FakeSqlRunner()))
        { StepTimeout = TimeSpan.FromMilliseconds(100) };
        var report = await diagnostics.RunAsync(Crate());
        Assert.Equal("Port open", report.Failed!.Name);
        Assert.Contains("did not answer in time", report.Failed.Detail);
        Assert.Contains("firewall", report.Hint);
    }

    [Fact]
    public async Task ARejectedPassword_NamesTheUser_AndGivesTheDatabasesStartingHint()
    {
        var diagnostics = new ConnectionDiagnostics(Resolves, Connects,
            c => new CrateDbClient(c, new FailingRunner(1, new PostgresException("bad", "FATAL", "FATAL", "28P01"))));
        var report = await diagnostics.RunAsync(Crate(user: "alice"));
        Assert.Equal("Sign in", report.Failed!.Name);
        Assert.Equal("The password was rejected for \"alice\".", report.Failed.Detail);
        Assert.Contains("A new CrateDB accepts the user crate with no password.", report.Hint);
        Assert.Equal(new[] { StepStatus.Ok, StepStatus.Ok, StepStatus.Failed }, report.Steps.Select(s => s.Status));
    }

    [Fact]
    public async Task APasswordHint_IsSpecificToTheDatabase()
    {
        var timescale = new DatabaseConnection("1", "c", "h", 5433, "postgres", "x") { Kind = DatabaseKind.TimescaleDb };
        var (_, hint) = ConnectionHints.Explain(new PostgresException("bad", "FATAL", "FATAL", "28P01"), timescale);
        Assert.Contains("postgres", hint);
        Assert.DoesNotContain("CrateDB", hint);
    }

    [Fact]
    public async Task AMissingDatabase_SuggestsClearingTheField()
    {
        var diagnostics = new ConnectionDiagnostics(Resolves, Connects,
            c => new CrateDbClient(c, new FailingRunner(1, new PostgresException("db", "FATAL", "FATAL", "3D000"))));
        var report = await diagnostics.RunAsync(Crate(database: "nope"));
        Assert.Equal("The database \"nope\" does not exist.", report.Failed!.Detail);
        Assert.Contains("Clear the Database field", report.Hint);
    }

    [Fact]
    public async Task APasswordThatIsStillBlank_IsNotMistakenForAnotherFailure()
    {
        var report = await Healthy(new FakeSqlRunner()).RunAsync(Crate(user: null));
        Assert.True(report.Success);
    }

    [Fact]
    public async Task APing_ThatFails_DoesNotFailTheTest()
    {
        var report = await Healthy(new FakeSqlRunner()).RunAsync(Crate());
        Assert.True(report.Success);
        Assert.Equal("CrateDB", report.Version);
    }

    [Fact]
    public async Task ACancelledTest_StopsWithoutAReport()
    {
        using var source = new CancellationTokenSource();
        var diagnostics = new ConnectionDiagnostics(Resolves, async (_, _, token) => { source.Cancel(); await Task.Delay(Timeout.Infinite, token); },
            c => new CrateDbClient(c, new FakeSqlRunner()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => diagnostics.RunAsync(Crate(), source.Token));
    }

    [Fact]
    public async Task ADefaultPort_IsUsed_WhenNoneWasEntered()
    {
        int port = 0;
        var diagnostics = new ConnectionDiagnostics(Resolves, (_, p, _) => { port = p; return Task.CompletedTask; }, c => new CrateDbClient(c, new FakeSqlRunner()));
        await diagnostics.RunAsync(Crate(port: 0));
        Assert.Equal(5432, port);
    }

    [Fact]
    public async Task ANamedDatabase_IsReadDirectly_BecauseTheAccountMayNotListThemAll()
    {
        var runner = new FakeSqlRunner().Answer("pg_class", new[] { "schema_name", "table_name" }, new object[] { "public", "cpu" });
        var connection = new DatabaseConnection("1", "c", "h", 5433, "u", "p", false, "metrics") { Kind = DatabaseKind.TimescaleDb };
        var report = await new ConnectionDiagnostics(Resolves, Connects, c => new TimescaleDbClient(c, runner)).RunAsync(connection);

        Assert.True(report.Success);
        Assert.Equal(new[] { "metrics" }, report.Databases);
        Assert.Equal("Can read database \"metrics\"", report.Steps[2].Detail);
        Assert.DoesNotContain(runner.Queried, q => q.Sql.Contains("pg_database"));     // the list of databases was not asked for
        Assert.Contains(runner.Queried, q => q.Database == "metrics");
    }

    [Fact]
    public async Task ANamedDatabaseThatDoesNotExist_IsExplained()
    {
        var connection = new DatabaseConnection("1", "c", "h", 5433, "u", "p", false, "nope") { Kind = DatabaseKind.TimescaleDb };
        var failing = new FailingRunner(1, new PostgresException("db", "FATAL", "FATAL", "3D000"));
        var report = await new ConnectionDiagnostics(Resolves, Connects, c => new TimescaleDbClient(c, failing)).RunAsync(connection);
        Assert.Equal("The database \"nope\" does not exist.", report.Failed!.Detail);
        Assert.Empty(report.Databases);
    }

    [Fact]
    public async Task CrateDb_IgnoresADatabaseNameItDoesNotUse()
    {
        var runner = new FakeSqlRunner().Answer("sys.cluster", new[] { "name" }, new object[] { "prod" });
        var connection = new DatabaseConnection("1", "c", "h", 5434, "crate", "", false, "leftover") { Kind = DatabaseKind.CrateDb };
        var report = await new ConnectionDiagnostics(Resolves, Connects, c => new CrateDbClient(c, runner)).RunAsync(connection);
        Assert.Equal(new[] { "crate" }, report.Databases);
        Assert.Equal("Found 1 database", report.Steps[2].Detail);
    }

    // ------------------------------------------------------------------ the words

    public static IEnumerable<object?[]> Failures() => new[]
    {
        new object?[] { new HttpRequestException("x", null, HttpStatusCode.Unauthorized), "rejected the credentials (HTTP 401)", "token" },
        new object?[] { new HttpRequestException("x", null, HttpStatusCode.Forbidden), "rejected the credentials (HTTP 403)", "token" },
        new object?[] { new AuthenticationException("The remote certificate is invalid"), "secure connection (TLS) could not be set up", "Use SSL off" },
        new object?[] { new InvalidOperationException("The SSL connection could not be established"), "SSL connection", "Use SSL off" },
        new object?[] { new TimeoutException(), "did not answer in time", "firewall" },
        new object?[] { new SocketException((int)SocketError.ConnectionRefused), "Nothing is listening on", "running" },
        new object?[] { new AggregateException(new SocketException((int)SocketError.ConnectionRefused)), "Nothing is listening on", "running" },
        new object?[] { new NpgsqlException("Exception while connecting", new SocketException((int)SocketError.ConnectionRefused)), "Nothing is listening on", "running" },
        new object?[] { new PostgresException("denied", "ERROR", "ERROR", "42501"), "not allowed to do that", "more rights" },
        new object?[] { new InvalidOperationException("401 Unauthorized"), "401 Unauthorized", "token" },
        new object?[] { new InvalidOperationException("something odd"), "something odd", null },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void AFailure_IsExplained_InPlainWords(Exception error, string detail, string? hint)
    {
        var influx = new DatabaseConnection("1", "c", "influx.example.com", 8181, null, null) { Kind = DatabaseKind.InfluxDb3, Token = "t" };
        var (message, advice) = ConnectionHints.Explain(error, influx);
        Assert.Contains(detail, message);
        if (hint == null) Assert.Null(advice);
        else Assert.Contains(hint, advice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheDriversWrappers_AreSeenThrough()
    {
        var inner = new SocketException((int)SocketError.ConnectionRefused);
        Assert.Same(inner, ConnectionHints.Unwrap(new AggregateException(new NpgsqlException("x", inner))));
        var pg = new PostgresException("m", "E", "E", "28P01");
        Assert.Same(pg, ConnectionHints.Unwrap(pg));
    }

    [Fact]
    public void EveryDatabase_HasASignInHint()
    {
        foreach (var profile in Enum.GetValues<DatabaseKind>().Select(DatabaseProfiles.Get))
            Assert.False(string.IsNullOrWhiteSpace(profile.SignInHint), profile.DisplayName);
    }
}
