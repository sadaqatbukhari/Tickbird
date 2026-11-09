using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Tickbird.Core.Data
{
    /// <summary>
    /// Base class for concrete implentnation to InfluxDB clients.
    /// </summary>
    public abstract class DatabaseClient
    {
        #region Fields

        #endregion Fields

        #region Properties

        /// <summary>
        /// Gets the InfluxDB connection configuration used by the client.
        /// </summary>
        public DatabaseConnection Connection { get; private set; }

        #endregion Properties

        #region Constructors

        /// <summary>
        /// Creates an InfluxDB client using the given InfluxDB connection information.
        /// </summary>
        /// <param name="connection">The InfluxDB connection configuration to use with the client.</param>
        public DatabaseClient(DatabaseConnection connection)
        {
            if (connection == null) throw new ArgumentNullException("connection");
            Connection = connection;
        }

        #endregion Constructors

        #region Methods

        #region Databases

        /// <summary>
        /// Gets a list of database names for the current connection.
        /// </summary>
        /// <returns>A list of database names in the current connection.</returns>
        public abstract Task<IEnumerable<string>> GetDatabaseNamesAsync();

        /// <summary>
        /// Creates a new database with the given name.
        /// </summary>
        /// <param name="database">The name of the database to create.</param>
        /// <returns>An API response for the write request.</returns>
        public abstract Task<CommandResult> CreateDatabaseAsync(string database);

        /// <summary>
        /// Drops an existing database given its name.
        /// </summary>
        /// <param name="database">The name of the database to drop.</param>
        /// <returns>An API response for the write request.</returns>
        public abstract Task<CommandResult> DropDatabaseAsync(string database);

        #endregion Databases

        #region Retention Policies

        /// <summary>
        /// Gets the retention policies for a given database.
        /// </summary>
        /// <param name="database">The name of the database to get retention policies for.</param>
        /// <returns>The list of retention policies for the database.</returns>
        public abstract Task<IEnumerable<RetentionPolicy>> GetRetentionPoliciesAsync(string database);

        /// <summary>
        /// Creates a new retention policy for a given database.
        /// </summary>
        /// <param name="database">The name of the database to create the retention policy for.</param>
        /// <param name="policyName">The policy's name.</param>
        /// <param name="duration">The retention duration of data.</param>
        /// <param name="replication">The replication number for the policy.</param>
        /// <param name="isDefault">Whether or not this will be the default policy for the database.</param>
        /// <returns>The API response.</returns>
        public abstract Task<CommandResult> CreateRetentionPolicyAsync(string database, string policyName, string duration, int replication, bool isDefault = false);

        /// <summary>
        /// Alters an existing retention policy for a given database.
        /// </summary>
        /// <param name="database">The name of the database the retention policy belongs to.</param>
        /// <param name="policyName">The policy's name to alter.</param>
        /// <param name="duration">The retention duration of data.</param>
        /// <param name="replication">The replication number for the policy.</param>
        /// <param name="isDefault">Whether or not this will be the default policy for the database.</param>
        /// <returns>The API response.</returns>
        public abstract Task<CommandResult> AlterRetentionPolicyAsync(string database, string policyName, string duration, int replication, bool isDefault = false);

        /// <summary>
        /// Drops an existing retention policy.
        /// </summary>
        /// <param name="database">The name of the database the retention policy belongs to.</param>
        /// <param name="policyName">The name of the retention policy to drop.</param>
        /// <returns>The API response.</returns>
        public abstract Task<CommandResult> DropRetentionPolicyAsync(string database, string policyName);

        #endregion Retention Policies

        #region Measurements

        /// <summary>
        /// Gets a list of database names for the current connection.
        /// </summary>
        /// <param name="database">The name of the database to get measurement names for.</param>
        /// <returns>A list of measurement names for the given database.</returns>
        public abstract Task<IEnumerable<string>> GetMeasurementNamesAsync(string database);

        /// <summary>
        /// Drops the series in a particular database and optional measurement.
        /// </summary>
        /// <param name="database">The database to drop the series from.</param>
        /// <param name="measurement">The measurement to drop the series from.</param>
        /// <returns>An API response for the write request.</returns>
        public abstract Task<CommandResult> DropMeasurementAsync(string database, string measurement);

        /// <summary>
        /// Gets the list of tag keys for a specified measurement.
        /// </summary>
        /// <param name="database">The database to use.</param>
        /// <param name="measurement">The measurment to get tag keys for.</param>
        /// <returns>A list of tag keys/names for the specified measurment.</returns>
        public abstract Task<IEnumerable<string>> GetTagKeysAsync(string database, string measurement);

        /// <summary>
        /// Gets the list of tag values for a specified measurement and tag.
        /// </summary>
        /// <param name="database">The database to use.</param>
        /// <param name="measurement">The measurment to get tag keys for.</param>
        /// <param name="tag">The name of the tag to get values for.</param>
        /// <returns>A list of tag keys/names for the specified measurment.</returns>
        public abstract Task<IEnumerable<TagValue>> GetTagValuesAsync(string database, string measurement, string tag);

        /// <summary>
        /// Gets the list of field keys for a specified measurement.
        /// </summary>
        /// <param name="database">The database to use.</param>
        /// <param name="measurement">The measurment to get field keys for.</param>
        /// <returns>A list of field keys/names for the specified measurment.</returns>
        public abstract Task<IEnumerable<FieldKey>> GetFieldKeysAsync(string database, string measurement);

        /// <summary>
        /// Gets every measurement in a database with its field and tag names (used by query IntelliSense).
        /// The default asks per measurement; clients override it with a cheaper whole-database query.
        /// </summary>
        public virtual async Task<IReadOnlyList<MeasurementSchema>> GetDatabaseSchemaAsync(string database)
        {
            var names = (await GetMeasurementNamesAsync(database) ?? Enumerable.Empty<string>()).ToList();
            var fieldSeries = new List<ResultSeries>();
            var tagSeries = new List<ResultSeries>();
            foreach (var name in names)
            {
                try
                {
                    var fields = await GetFieldKeysAsync(database, name) ?? Enumerable.Empty<FieldKey>();
                    fieldSeries.Add(new ResultSeries(name, new List<string> { "fieldKey", "fieldType" }, new Dictionary<string, string>(),
                        fields.Select(f => (IList<object>)new List<object> { f.Name, f.Type }).ToList()));
                    var tags = await GetTagKeysAsync(database, name) ?? Enumerable.Empty<string>();
                    tagSeries.Add(new ResultSeries(name, new List<string> { "tagKey" }, new Dictionary<string, string>(),
                        tags.Select(t => (IList<object>)new List<object> { t }).ToList()));
                }
                catch (Exception) { /* one unreadable measurement must not hide the rest */ }
            }
            return MeasurementSchema.FromInfluxQl(fieldSeries, tagSeries, names);
        }

        /// <summary>Gets fields from a specific retention policy, where supported.</summary>
        public virtual Task<IEnumerable<FieldKey>> GetFieldKeysAsync(
            string database, string measurement, string retentionPolicy) => GetFieldKeysAsync(database, measurement);

        #endregion Measurements

        #region Series

        /// <summary>
        /// Gets a list of series set names for a given database and optionally measurement.
        /// </summary>
        /// <param name="database">The database to get series sets for.</param>
        /// <param name="measurement">The measurement to get series sets for.</param>
        /// <returns>A list of series names.</returns>
        public abstract Task<IEnumerable<string>> GetSeriesNamesAsync(string database, string measurement = null);

        /// <summary>
        /// Drops the series in a particular database and optional measurement.
        /// </summary>
        /// <param name="database">The database to drop the series from.</param>
        /// <param name="measurement">The measurement to drop the series from.</param>
        /// <returns>An API response for the write request.</returns>
        public abstract Task<CommandResult> DropSeriesAsync(string database, string measurement = null);

        #endregion Series

        #region Query

        /// <summary>
        /// Gets a list of the currently running queries.
        /// </summary>
        /// <returns>A list of the currently running queries.</returns>
        public abstract Task<IEnumerable<RunningQuery>> GetRunningQueriesAsync();

        /// <summary>
        /// Kills a running query given its process ID.
        /// </summary>
        /// <returns>The API response.</returns>
        public abstract Task<CommandResult> KillQueryAsync(int pid);

        /// <summary>
        /// Executes a query against the connection for the given database.
        /// </summary>
        /// <param name="database">The name of the database to query.</param>
        /// <param name="query">The query to execute.</param>
        /// <returns>The result of the query.</returns>
        public abstract Task<IEnumerable<ResultSeries>> QueryAsync(string database, string query);

        /// <summary>
        /// Executes a query that can be cancelled (Stop button or query timeout). Clients that can abort the
        /// request override this; the default stops waiting for the result.
        /// </summary>
        public virtual Task<IEnumerable<ResultSeries>> QueryAsync(string database, string query, CancellationToken cancellationToken) =>
            cancellationToken.CanBeCanceled ? QueryAsync(database, query).WaitAsync(cancellationToken) : QueryAsync(database, query);

        /// <summary>
        /// Gets the continuous queries for a given database.
        /// </summary>
        /// <param name="database">The name of the database.</param>
        /// <returns>The database's continous queries.</returns>
        public abstract Task<IEnumerable<ContinuousQuery>> GetContinousQueriesAsync(string database);

        /// <summary>
        /// Creates a new Influx DB Continuous Query.
        /// </summary>
        /// <param name="cqParams">The parameters used to create the CQ.</param>
        /// <returns>The API response.</returns>
        public abstract Task<CommandResult> CreateContinuousQueryAsync(ContinuousQueryParams cqParams);

        /// <summary>
        /// Drops an existing Influx DB Continuous Query.
        /// </summary>
        /// <param name="database">The name of the database the Continuous Query belongs to.</param>
        /// <param name="cqName">The name of the Continuous Query to drop.</param>
        /// <returns>The API response.</returns>
        public abstract Task<CommandResult> DropContinuousQueryAsync(string database, string cqName);

        /// <summary>
        /// Performs a backfill query on the database based on a given time range and backfill parameters.
        /// </summary>
        /// <param name="database">The name of the database to perform the backfill on.</param>
        /// <param name="backfillParams">The backfill parameters query.</param>
        /// <returns>The API response.</returns>
        public abstract Task<CommandResult> BackfillAsync(string database, BackfillParams backfillParams);

        #endregion Query

        #region Write

        /// <summary>
        /// Writes a point to the specified database and measurement.
        /// </summary>
        /// <param name="database">The name of the database to write to.</param>
        /// <param name="measurement">The name of the measurement to write to.</param>
        /// <param name="tags">The list of tag values to write.</param>
        /// <param name="fields">The list of field values to write.</param>
        /// <param name="timeStamp">The point's time stamp.</param>
        /// <param name="retentionPolicy">The retention policy to write to.</param>
        /// <returns>An API response for the write request.</returns>
        public abstract Task<CommandResult> WriteAsync(string database, string measurement,
            IDictionary<string, object> tags, IDictionary<string, object> fields, DateTime timeStamp, string retentionPolicy = null);

        /// <summary>
        /// Writes a point to the specified database and measurement.
        /// </summary>
        /// <param name="database">The name of the database to write to.</param>
        /// <param name="point">The InfluxDB point to write.</param>
        /// <param name="retentionPolicy">The retention policy to write to.</param>
        /// <returns>An API response for the write request.</returns>
        public abstract Task<CommandResult> WriteAsync(string database, DataPoint point, string retentionPolicy = null);

        /// <summary>
        /// Writes a list of points to the specified database.
        /// </summary>
        /// <param name="database">The name of the database to write to.</param>
        /// <param name="points">The InfluxDB points to write.</param>
        /// <param name="retentionPolicy">The retention policy to write to.</param>
        /// <returns>An API response for the write request.</returns>
        public abstract Task<CommandResult> WriteAsync(string database, IEnumerable<DataPoint> points, string retentionPolicy = null);

        #endregion Write

        #region Records

        /// <summary>
        /// The columns of a table or measurement and which of them identify a record (see <see cref="RecordSchema"/>).
        /// Databases that cannot edit records do not override this.
        /// </summary>
        public virtual Task<RecordSchema> GetRecordSchemaAsync(string database, string table) =>
            throw new NotSupportedException("Editing records is not supported for this database.");

        /// <summary>Inserts one record. <paramref name="values"/> are the columns to set; columns left out get their default.</summary>
        public virtual Task<CommandResult> InsertRecordAsync(string database, RecordSchema schema, IReadOnlyDictionary<string, object> values) =>
            throw new NotSupportedException("Inserting records is not supported for this database.");

        /// <summary>
        /// Changes one record. <paramref name="original"/> holds the record as it was read (it finds the record);
        /// <paramref name="changes"/> holds only the columns that were edited. Fails, without changing anything, when the
        /// record cannot be found or is not the only one that matches.
        /// </summary>
        public virtual Task<CommandResult> UpdateRecordAsync(string database, RecordSchema schema,
            IReadOnlyDictionary<string, object> original, IReadOnlyDictionary<string, object> changes) =>
            throw new NotSupportedException("Changing records is not supported for this database.");

        /// <summary>Deletes one record, found from <paramref name="original"/>. Fails, without deleting anything, when it cannot be found or is not the only match.</summary>
        public virtual Task<CommandResult> DeleteRecordAsync(string database, RecordSchema schema, IReadOnlyDictionary<string, object> original) =>
            throw new NotSupportedException("Deleting records is not supported for this database.");

        #endregion Records

        #region Server

        /// <summary>
        /// Sends a ping request message to the InfluxDB server and returns the pong response.
        /// </summary>
        /// <returns>The server's response to the ping request.</returns>
        public abstract Task<PingResponse> PingAsync();

        /// <summary>
        /// Gets diagnostic information from the InfluxDB server.
        /// </summary>
        /// <returns>The server's diagnostics information.</returns>
        public abstract Task<ServerDiagnostics> GetDiagnosticsAsync();

        /// <summary>
        /// Gets server statistics from the InfluxDB server.
        /// </summary>
        /// <returns>The server's statistics.</returns>
        public abstract Task<ServerStats> GetStatsAsync();

        #endregion Server

        #region Users

        /// <summary>
        /// Gets a list of the current system users.
        /// </summary>
        /// <returns>A list of system users.</returns>
        public abstract Task<IEnumerable<DatabaseUser>> GetUsersAsync();

        /// <summary>
        /// Creates a new InfluxDB user with the given user name, password, and admin privileges.
        /// </summary>
        /// <param name="username">The user's name.</param>
        /// <param name="password">The user's password.</param>
        /// <param name="isAdmin">Whether or not to grant administrator privileges to the user.</param>
        /// <returns>The API response.</returns>
        public abstract Task<CommandResult> CreateUserAsync(string username, string password, bool isAdmin);

        /// <summary>
        /// Drops an existing InfluxDB user with the given user name.
        /// </summary>
        /// <param name="username">The user's name.</param>
        /// <returns>The API response.</returns>
        public abstract Task<CommandResult> DropUserAsync(string username);

        /// <summary>
        /// Sets a user's password.
        /// </summary>
        /// <param name="username">The user's name.</param>
        /// <param name="password">The password to set.</param>
        /// <returns>The API response.</returns>
        public abstract Task<CommandResult> SetPasswordAsync(string username, string password);

        /// <summary>
        /// Gets a list of granted database privileges for a user.
        /// </summary>
        /// <param name="username">The name of the user to get granted privilges for.</param>
        /// <returns>A list of granted privileges for the user.</returns>
        public abstract Task<IEnumerable<DatabaseGrant>> GetPrivilegesAsync(string username);

        /// <summary>
        /// Grants administrator privileges to a user.
        /// </summary>
        /// <param name="username">The name of the user to grant privileges to.</param>
        /// <returns>The API response.</returns>
        public abstract Task<CommandResult> GrantAdministratorAsync(string username);
        
        /// <summary>
        /// Revokes administrator privileges from a user.
        /// </summary>
        /// <param name="username">The name of the user to revoke privileges from.</param>
        /// <returns>The API response.</returns>
        public abstract Task<CommandResult> RevokeAdministratorAsync(string username);

        /// <summary>
        /// Grants a privilege to a user for a given database.
        /// </summary>
        /// <param name="username">The user's name.</param>
        /// <param name="privilege">The privilege to grant.</param>
        /// <param name="database">The name of the database the privilege is for.</param>
        /// <returns>The query response.</returns>
        public abstract Task<CommandResult> GrantPrivilegeAsync(string username, DatabasePrivileges privilege, string database);

        /// <summary>
        /// Revokes a privilege from a user for a given database.
        /// </summary>
        /// <param name="username">The user's name.</param>
        /// <param name="privilege">The privilege to revoke.</param>
        /// <param name="database">The name of the database the privilege should be revoked from.</param>
        /// <returns>The query response.</returns>
        public abstract Task<CommandResult> RevokePrivilegeAsync(string username, DatabasePrivileges privilege, string database);

        #endregion Users

        #endregion Methods
    }
}
