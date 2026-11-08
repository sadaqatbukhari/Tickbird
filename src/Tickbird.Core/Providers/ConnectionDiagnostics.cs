using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using Npgsql;
using Tickbird.Core.Data;

namespace Tickbird.Core.Providers;

public enum StepStatus { Ok, Failed, Skipped }

/// <summary>One check of a connection test: <c>Host found</c>, <c>Port open</c>, <c>Sign in</c>.</summary>
public sealed record DiagnosticStep(string Name, StepStatus Status, string Detail, TimeSpan Elapsed);

/// <summary>What a connection test found.</summary>
public sealed class DiagnosticReport
{
    public DiagnosticReport(IReadOnlyList<DiagnosticStep> steps, string? hint, IReadOnlyList<string> databases, string? version, TimeSpan latency)
    {
        Steps = steps;
        Hint = hint;
        Databases = databases;
        Version = version;
        Latency = latency;
    }

    public IReadOnlyList<DiagnosticStep> Steps { get; }

    /// <summary>What to try, for the step that failed.</summary>
    public string? Hint { get; }

    /// <summary>The databases the account can see, when signing in worked.</summary>
    public IReadOnlyList<string> Databases { get; }

    public string? Version { get; }

    public TimeSpan Latency { get; }

    public bool Success => Steps.All(s => s.Status == StepStatus.Ok);

    public DiagnosticStep? Failed => Steps.FirstOrDefault(s => s.Status == StepStatus.Failed);
}

/// <summary>Turns an exception of a connection attempt into words and a next step.</summary>
public static class ConnectionHints
{
    /// <summary>The message and the hint for a failure to connect or sign in.</summary>
    public static (string Detail, string? Hint) Explain(Exception error, DatabaseConnection connection)
    {
        var profile = connection.Profile;
        var user = string.IsNullOrWhiteSpace(connection.Username) ? "the user" : $"\"{connection.Username}\"";
        var root = Unwrap(error);
        var address = $"{AddressOf(connection)}";

        switch (root)
        {
            case PostgresException pg when pg.SqlState is "28P01" or "28000":
                return ($"The password was rejected for {user}.", Join("Check the user name and password.", profile.SignInHint));
            case PostgresException pg when pg.SqlState == "3D000":
                return ($"The database \"{connection.Database}\" does not exist.", "Clear the Database field to list the databases on the server, then pick one.");
            case PostgresException pg when pg.SqlState == "42501":
                return ($"{Capitalise(user)} is not allowed to do that.", "Sign in with an account that has more rights, such as an administrator.");
            case PostgresException pg:
                return (pg.MessageText, null);
            case SocketException { SocketErrorCode: SocketError.ConnectionRefused }:
                return ($"Nothing is listening on {address}.",
                    $"Check that the server is running and the port is published. {profile.DisplayName} uses port {profile.DefaultPort} by default.");
            case SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData }:
                return ($"No server was found for \"{connection.Host}\".", "Check the host name for typing mistakes.");
            case SocketException { SocketErrorCode: SocketError.TimedOut } or TimeoutException or OperationCanceledException:
                return ($"{address} did not answer in time.", "Check the host and port, and that no firewall blocks the connection.");
            case AuthenticationException:
                return ("The secure connection (TLS) could not be set up.",
                    "Turn Use SSL off for a local server, or allow untrusted certificates in Settings for a self-signed one.");
            case HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } http:
                return ($"The server rejected the credentials (HTTP {(int)http.StatusCode!}).", Join("Check the user, password or token.", profile.SignInHint));
        }

        var message = root.Message;
        if (message.Contains("SSL", StringComparison.OrdinalIgnoreCase) || message.Contains("TLS", StringComparison.OrdinalIgnoreCase))
            return (message, "Turn Use SSL off for a local server, or allow untrusted certificates in Settings for a self-signed one.");
        if (message.Contains("401") || message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) || message.Contains("authentication", StringComparison.OrdinalIgnoreCase))
            return (message, Join("Check the user, password or token.", profile.SignInHint));
        return (message, null);
    }

    private static string Join(string first, string? second) => string.IsNullOrWhiteSpace(second) ? first : first + " " + second;

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    public static string AddressOf(DatabaseConnection connection) => $"{AdvisorHost(connection)}:{(connection.Port == 0 ? connection.Profile.DefaultPort : connection.Port)}";

    private static string AdvisorHost(DatabaseConnection connection) => ConnectionAdvisor.CleanHost(connection.Host);

    /// <summary>The exception that says what went wrong, under the wrappers a driver puts around it.</summary>
    public static Exception Unwrap(Exception error)
    {
        var current = error;
        while (true)
        {
            if (current is PostgresException) return current;
            if (current is AggregateException { InnerExceptions.Count: > 0 } aggregate) current = aggregate.InnerExceptions[0];
            else if (current.InnerException != null && current is NpgsqlException or HttpRequestException or IOException or InvalidOperationException) current = current.InnerException!;
            else if (current.InnerException is SocketException or AuthenticationException or TimeoutException) current = current.InnerException;
            else return current;
        }
    }
}

/// <summary>Tests a connection. The dialog depends on this, so tests can replace the network.</summary>
public interface IConnectionTester
{
    Task<DiagnosticReport> RunAsync(DatabaseConnection connection, CancellationToken cancellationToken = default);
}

/// <summary>
/// Tests a connection one step at a time (host found, port open, sign in) and says which step failed and what to try. The
/// network and the client are passed in, so tests run it without a server.
/// </summary>
public sealed class ConnectionDiagnostics : IConnectionTester
{
    private readonly Func<string, CancellationToken, Task> resolve;
    private readonly Func<string, int, CancellationToken, Task> connect;
    private readonly Func<DatabaseConnection, DatabaseClient> createClient;

    /// <summary>How long each step may take.</summary>
    public TimeSpan StepTimeout { get; init; } = TimeSpan.FromSeconds(8);

    public ConnectionDiagnostics(Func<string, CancellationToken, Task>? resolve = null,
        Func<string, int, CancellationToken, Task>? connect = null, Func<DatabaseConnection, DatabaseClient>? createClient = null)
    {
        this.resolve = resolve ?? (async (host, token) => await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false));
        this.connect = connect ?? (async (host, port, token) =>
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, token).ConfigureAwait(false);
        });
        this.createClient = createClient ?? DatabaseClientFactory.Create;
    }

    public async Task<DiagnosticReport> RunAsync(DatabaseConnection connection, CancellationToken cancellationToken = default)
    {
        var profile = connection.Profile;
        var host = ConnectionAdvisor.CleanHost(connection.Host);
        var port = connection.Port == 0 ? profile.DefaultPort : connection.Port;
        var steps = new List<DiagnosticStep>();
        string? hint = null;

        // 1. the host name
        if (System.Net.IPAddress.TryParse(host, out _)) steps.Add(new("Host found", StepStatus.Ok, "An IP address", TimeSpan.Zero));
        else if (!await StepAsync(steps, "Host found", host, token => resolve(host, token), connection, cancellationToken, h => hint = h).ConfigureAwait(false))
            return Skipped(steps, hint, "Port open", "Sign in");

        // 2. the port
        if (!await StepAsync(steps, "Port open", $"{host}:{port}", token => connect(host, port, token), connection, cancellationToken, h => hint = h).ConfigureAwait(false))
            return Skipped(steps, hint, "Sign in");

        // 3. sign in and read the databases
        IReadOnlyList<string> databases = Array.Empty<string>();
        string? version = null;
        var latency = TimeSpan.Zero;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(StepTimeout);
            var client = createClient(connection);
            // A named database is read directly: some accounts may use one database but not list them all.
            var named = profile.UsesDatabaseName && !string.IsNullOrWhiteSpace(connection.Database) ? connection.Database : null;
            var read = named == null
                ? client.GetDatabaseNamesAsync()
                : ReadOneAsync(client, named);
            var finished = await Task.WhenAny(read, Task.Delay(StepTimeout, timeout.Token)).ConfigureAwait(false);
            if (finished != read)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException();
            }
            databases = (await read.ConfigureAwait(false)).ToList();
            watch.Stop();
            latency = watch.Elapsed;
            var ping = await client.PingAsync().ConfigureAwait(false);
            if (ping.Success) { version = ping.Version; latency = ping.ResponseTime; }
            steps.Add(new("Sign in", StepStatus.Ok,
                named != null ? $"Can read database \"{named}\"" : $"Found {databases.Count} database{(databases.Count == 1 ? "" : "s")}", watch.Elapsed));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var (detail, h) = ConnectionHints.Explain(ex, connection);
            steps.Add(new("Sign in", StepStatus.Failed, detail, watch.Elapsed));
            hint = h;
        }
        return new DiagnosticReport(steps, hint, databases, version, latency);
    }

    private async Task<bool> StepAsync(List<DiagnosticStep> steps, string name, string subject, Func<CancellationToken, Task> work,
        DatabaseConnection connection, CancellationToken cancellationToken, Action<string?> setHint)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(StepTimeout);
            await work(timeout.Token).ConfigureAwait(false);
            steps.Add(new(name, StepStatus.Ok, subject, watch.Elapsed));
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var (detail, hint) = ConnectionHints.Explain(ex, connection);
            steps.Add(new(name, StepStatus.Failed, detail, watch.Elapsed));
            setHint(hint);
            return false;
        }
    }

    /// <summary>Reads the tables of one database, which proves the account may use it, and reports that database.</summary>
    private static async Task<IEnumerable<string>> ReadOneAsync(DatabaseClient client, string database)
    {
        await client.GetMeasurementNamesAsync(database).ConfigureAwait(false);
        return new[] { database };
    }

    private static DiagnosticReport Skipped(List<DiagnosticStep> steps, string? hint, params string[] rest)
    {
        steps.AddRange(rest.Select(name => new DiagnosticStep(name, StepStatus.Skipped, "Not tried", TimeSpan.Zero)));
        return new DiagnosticReport(steps, hint, Array.Empty<string>(), null, TimeSpan.Zero);
    }
}
