namespace Tickbird.Core.Data.Sql;

/// <summary>
/// Runs SQL for a client of the PostgreSQL family. The real one talks to the server; tests use a fake that records
/// the statements and returns canned rows, so the SQL a client writes can be checked without a database.
/// </summary>
public interface ISqlRunner
{
    /// <summary>Runs the statements and returns one <see cref="ResultSeries"/> per result set.</summary>
    /// <param name="database">The database to connect to, or null for the connection's own database.</param>
    Task<IReadOnlyList<ResultSeries>> QueryAsync(string? database, string sql,
        IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default);

    /// <summary>Runs statements that return no rows. Returns the number of affected rows.</summary>
    Task<int> ExecuteAsync(string? database, string sql,
        IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default);
}
