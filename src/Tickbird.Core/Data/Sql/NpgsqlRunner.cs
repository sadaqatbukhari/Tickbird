using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace Tickbird.Core.Data.Sql;

/// <summary>Runs SQL with Npgsql, keeping one connection pool per database.</summary>
public sealed class NpgsqlRunner : ISqlRunner, IDisposable
{
    private readonly DatabaseConnection connection;
    private readonly string defaultDatabase;
    private readonly ConcurrentDictionary<string, NpgsqlDataSource> sources = new(StringComparer.Ordinal);

    /// <param name="connection">Host, port, user, password and SSL setting.</param>
    /// <param name="defaultDatabase">The database used when none is named and the connection names none (for example "postgres").</param>
    public NpgsqlRunner(DatabaseConnection connection, string defaultDatabase)
    {
        this.connection = connection;
        this.defaultDatabase = defaultDatabase;
    }

    /// <summary>The connection settings for a database.</summary>
    public static NpgsqlConnectionStringBuilder BuildConnectionString(DatabaseConnection connection, string database, bool trustServerCertificate)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = connection.Host,
            Port = connection.Port == 0 ? 5432 : connection.Port,
            Database = database,
            Username = string.IsNullOrWhiteSpace(connection.Username) ? null : connection.Username,
            Password = string.IsNullOrEmpty(connection.Password) ? null : connection.Password,
            SslMode = connection.UseSsl ? SslMode.Require : SslMode.Prefer,
            Timeout = 15,
            CommandTimeout = 0,               // the query timeout setting cancels long queries instead
            ApplicationName = "Tickbird",
            Pooling = true,
            MaxPoolSize = 8,
        };
        if (connection.UseSsl && trustServerCertificate) builder.TrustServerCertificate = true;
        return builder;
    }

    private NpgsqlDataSource SourceFor(string? database)
    {
        var name = !string.IsNullOrWhiteSpace(database) ? database
            : !string.IsNullOrWhiteSpace(connection.Database) ? connection.Database : defaultDatabase;
        return sources.GetOrAdd(name, n =>
            NpgsqlDataSource.Create(BuildConnectionString(connection, n, DatabaseClientFactory.AllowUntrustedSsl()).ConnectionString));
    }

    public async Task<IReadOnlyList<ResultSeries>> QueryAsync(string? database, string sql,
        IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        await using var command = SourceFor(database).CreateCommand(sql);
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<ResultSeries>();
        do
        {
            var columns = new List<string>();
            for (var i = 0; i < reader.FieldCount; i++) columns.Add(reader.GetName(i));
            if (columns.Count == 0) continue;     // a statement without a result (SET, CREATE...) between selects
            var rows = new List<IList<object>>();
            var values = new object[reader.FieldCount];
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var count = reader.GetValues(values);
                var row = new List<object>(count);
                for (var i = 0; i < count; i++) row.Add(ToCellValue(values[i]));
                rows.Add(row);
            }
            results.Add(new ResultSeries(results.Count == 0 ? "result" : "result " + (results.Count + 1), columns,
                new Dictionary<string, string>(), rows));
        } while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
        return results;
    }

    public async Task<int> ExecuteAsync(string? database, string sql,
        IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        await using var command = SourceFor(database).CreateCommand(sql);
        AddParameters(command, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Closes this runner's connections to a database (before the database is dropped).</summary>
    public void Release(string database)
    {
        if (sources.TryRemove(database, out var source)) source.Dispose();
    }

    /// <summary>Opens a connection and returns how long it took, for Ping.</summary>
    public async Task<TimeSpan> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        await using var open = await SourceFor(null).OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return watch.Elapsed;
    }

    private static void AddParameters(NpgsqlCommand command, IReadOnlyDictionary<string, object?>? parameters)
    {
        if (parameters == null) return;
        foreach (var (name, value) in parameters)
            command.Parameters.Add(value is UntypedText untyped
                ? new NpgsqlParameter(name, NpgsqlDbType.Unknown) { Value = untyped.Value }
                : new NpgsqlParameter(name, value ?? DBNull.Value));
    }

    /// <summary>Cell values the grid, charts and exports understand: numbers, text, booleans and UTC dates; anything else as text.</summary>
    public static object? ToCellValue(object? value) => value switch
    {
        null or DBNull => null,
        string or bool or long or int or short or byte or sbyte or double or float or decimal => value,
        DateTime dateTime => dateTime.Kind == DateTimeKind.Utc ? dateTime : DateTime.SpecifyKind(dateTime, DateTimeKind.Utc),
        DateTimeOffset offset => offset.UtcDateTime,
        DateOnly date => new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Utc),
        byte[] bytes => "\\x" + Convert.ToHexString(bytes).ToLowerInvariant(),
        Array array => "{" + string.Join(",", array.Cast<object?>().Select(x => Convert.ToString(ToCellValue(x), CultureInfo.InvariantCulture))) + "}",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
    };

    public void Dispose()
    {
        foreach (var source in sources.Values) source.Dispose();
        sources.Clear();
    }
}
