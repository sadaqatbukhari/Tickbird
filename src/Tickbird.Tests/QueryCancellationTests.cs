using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Tickbird.Core.Data;

namespace Tickbird.Tests;

/// <summary>A query can be stopped while the server has not answered (Stop button and query timeout).</summary>
public sealed class QueryCancellationTests : IDisposable
{
    private readonly TcpListener silentServer = new(IPAddress.Loopback, 0);
    private readonly List<TcpClient> accepted = new();

    public QueryCancellationTests()
    {
        silentServer.Start();
        // Accept connections and read nothing back: the query never gets an answer.
        _ = Task.Run(async () =>
        {
            try { while (true) accepted.Add(await silentServer.AcceptTcpClientAsync()); }
            catch (Exception) { /* listener stopped */ }
        });
    }

    public void Dispose()
    {
        silentServer.Stop();
        foreach (var client in accepted.ToList()) client.Dispose();
    }

    private DatabaseClient Client() => DatabaseClientFactory.Create(new DatabaseConnection(
        "t", "silent", "127.0.0.1", (ushort)((IPEndPoint)silentServer.LocalEndpoint).Port, null, null, useSsl: false));

    [Fact]
    public async Task InfluxDb1Query_StopsWhenCancelled()
    {
        var client = Client();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.QueryAsync("db", "SELECT * FROM slow", cancel.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task InfluxDb1Query_CanBeStoppedByHand()
    {
        var client = Client();
        using var cancel = new CancellationTokenSource();
        var query = client.QueryAsync("db", "SELECT * FROM slow", cancel.Token);
        await Task.Delay(200);
        Assert.False(query.IsCompleted);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
    }
}
