using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Apache.Arrow;
using InfluxDB3.Client.Config;
using InfluxDB3.Client.Write;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OfficialInfluxDb3Client = InfluxDB3.Client.InfluxDBClient;

namespace Tickbird.Core.Data
{
    /// <summary>
    /// HTTP API client for InfluxDB 3 Core and Enterprise.
    /// </summary>
    public sealed class InfluxDb3Client : DatabaseClient
    {
        private readonly OfficialInfluxDb3Client influx;
        private readonly HttpClient adminHttp;

        public InfluxDb3Client(DatabaseConnection connection) : base(connection)
        {
            var allowUntrusted = DatabaseClientFactory.AllowUntrustedSsl();
            influx = new OfficialInfluxDb3Client(new ClientConfig
            {
                Host = connection.HttpConnectionString,
                Token = connection.Token,
                Database = connection.Database,
                DisableServerCertificateValidation = allowUntrusted,
                WriteOptions = new WriteOptions { UseV2Api = false }
            });

            var handler = new HttpClientHandler();
            if (allowUntrusted)
                handler.ServerCertificateCustomValidationCallback = delegate { return true; };

            adminHttp = new HttpClient(handler)
            {
                BaseAddress = new Uri(connection.HttpConnectionString.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(100)
            };
            if (!string.IsNullOrWhiteSpace(connection.Token))
                adminHttp.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", connection.Token);
            adminHttp.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public override async Task<IEnumerable<string>> GetDatabaseNamesAsync()
        {
            var json = await SendAsync(HttpMethod.Get, "api/v3/configure/database?format=json");
            var token = JToken.Parse(json);
            var databases = token as JArray ?? token["databases"] as JArray ?? new JArray();
            return databases
                .Select(item => item.Type == JTokenType.String
                    ? (string)item
                    : (string)item["iox::database"] ?? (string)item["name"])
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();
        }

        public override async Task<CommandResult> CreateDatabaseAsync(string database)
        {
            Require(database, "database");
            return await SendForResponseAsync(HttpMethod.Post, "api/v3/configure/database",
                new JObject { ["db"] = database }.ToString(Formatting.None));
        }

        public override async Task<CommandResult> DropDatabaseAsync(string database)
        {
            Require(database, "database");
            return await SendForResponseAsync(HttpMethod.Delete,
                "api/v3/configure/database?db=" + Uri.EscapeDataString(database));
        }

        public override async Task<IEnumerable<string>> GetMeasurementNamesAsync(string database)
        {
            var series = await QuerySqlAsync(database,
                "SELECT table_name FROM information_schema.tables WHERE table_schema = 'iox' ORDER BY table_name");
            return ColumnValues(series, "table_name");
        }

        public override async Task<CommandResult> DropMeasurementAsync(string database, string measurement)
        {
            Require(database, "database");
            Require(measurement, "measurement");
            var path = "api/v3/configure/table?db=" + Uri.EscapeDataString(database)
                + "&table=" + Uri.EscapeDataString(measurement);
            return await SendForResponseAsync(HttpMethod.Delete, path);
        }

        public override async Task<IEnumerable<string>> GetTagKeysAsync(string database, string measurement)
        {
            var columns = await GetSchemaColumnsAsync(database, measurement);
            return columns.Where(c => IsTagType(c.Type)).Select(c => c.Name).ToList();
        }

        public override async Task<IEnumerable<TagValue>> GetTagValuesAsync(
            string database, string measurement, string tag)
        {
            Require(tag, "tag");
            var series = await QuerySqlAsync(database, "SELECT DISTINCT " + QuoteIdentifier(tag)
                + " FROM " + QuoteIdentifier(measurement) + " ORDER BY " + QuoteIdentifier(tag));
            return ColumnValues(series, tag).Select(value => new TagValue(tag, value)).ToList();
        }

        public override async Task<IEnumerable<FieldKey>> GetFieldKeysAsync(
            string database, string measurement)
        {
            var columns = await GetSchemaColumnsAsync(database, measurement);
            return columns.Where(c => !IsTagType(c.Type) && !string.Equals(c.Name, "time", StringComparison.OrdinalIgnoreCase))
                .Select(c => new FieldKey(c.Name, c.Type)).ToList();
        }

        /// <summary>Whole-database schema from a single information_schema query.</summary>
        public override async Task<IReadOnlyList<MeasurementSchema>> GetDatabaseSchemaAsync(string database)
        {
            Require(database, "database");
            var series = await QuerySqlAsync(database,
                "SELECT table_name, column_name, data_type FROM information_schema.columns "
                + "WHERE table_schema = 'iox' ORDER BY table_name, ordinal_position");
            return MeasurementSchema.FromInformationSchema(series);
        }

        public override async Task<IEnumerable<ResultSeries>> QueryAsync(string database, string query)
        {
            Require(database, "database");
            Require(query, "query");
            return await QuerySqlAsync(database, query);
        }

        public override async Task<IEnumerable<ResultSeries>> QueryAsync(string database, string query, CancellationToken cancellationToken)
        {
            Require(database, "database");
            Require(query, "query");
            // WaitAsync returns as soon as Stop is pressed, even while the server has not sent the first batch.
            return await QuerySqlAsync(database, query, cancellationToken).WaitAsync(cancellationToken);
        }

        public override async Task<CommandResult> WriteAsync(string database, string measurement,
            IDictionary<string, object> tags, IDictionary<string, object> fields, DateTime timeStamp,
            string retentionPolicy = null)
        {
            return await WriteAsync(database, new DataPoint(measurement, tags, fields, timeStamp), retentionPolicy);
        }

        public override Task<RecordSchema> GetRecordSchemaAsync(string database, string table) =>
            LineProtocolRecords.GetSchemaAsync(this, database, table);

        public override Task<CommandResult> InsertRecordAsync(string database, RecordSchema schema, IReadOnlyDictionary<string, object> values) =>
            LineProtocolRecords.InsertAsync(this, database, schema, values);

        public override Task<CommandResult> UpdateRecordAsync(string database, RecordSchema schema,
            IReadOnlyDictionary<string, object> original, IReadOnlyDictionary<string, object> changes) =>
            LineProtocolRecords.UpdateAsync(this, database, schema, false, original, changes);

        public override async Task<CommandResult> WriteAsync(
            string database, DataPoint point, string retentionPolicy = null)
        {
            return await WriteAsync(database, new[] { point }, retentionPolicy);
        }

        public override async Task<CommandResult> WriteAsync(
            string database, IEnumerable<DataPoint> points, string retentionPolicy = null)
        {
            Require(database, "database");
            if (points == null) throw new ArgumentNullException("points");
            await influx.WriteRecordsAsync(points.Select(ToLineProtocol), database, WritePrecision.Ns);
            return SuccessResponse();
        }

        public override async Task<PingResponse> PingAsync()
        {
            var watch = Stopwatch.StartNew();
            var version = await influx.GetServerVersion().ConfigureAwait(false);
            watch.Stop();
            return new PingResponse(true, watch.Elapsed,
                string.IsNullOrWhiteSpace(version) ? "3.x" : version);
        }

        public override Task<IEnumerable<RetentionPolicy>> GetRetentionPoliciesAsync(string database) =>
            Unsupported<IEnumerable<RetentionPolicy>>("Retention policies are replaced by database retention periods in InfluxDB 3.");
        public override Task<CommandResult> CreateRetentionPolicyAsync(string database, string policyName, string duration, int replication, bool isDefault = false) =>
            Unsupported<CommandResult>("InfluxDB 3 does not support InfluxDB 1.x retention policies.");
        public override Task<CommandResult> AlterRetentionPolicyAsync(string database, string policyName, string duration, int replication, bool isDefault = false) =>
            Unsupported<CommandResult>("InfluxDB 3 does not support InfluxDB 1.x retention policies.");
        public override Task<CommandResult> DropRetentionPolicyAsync(string database, string policyName) =>
            Unsupported<CommandResult>("InfluxDB 3 does not support InfluxDB 1.x retention policies.");
        public override Task<IEnumerable<string>> GetSeriesNamesAsync(string database, string measurement = null) =>
            Unsupported<IEnumerable<string>>("SHOW SERIES is an InfluxDB 1.x operation.");
        public override Task<CommandResult> DropSeriesAsync(string database, string measurement = null) =>
            Unsupported<CommandResult>("DROP SERIES is an InfluxDB 1.x operation.");
        public override Task<IEnumerable<RunningQuery>> GetRunningQueriesAsync() =>
            Unsupported<IEnumerable<RunningQuery>>("Use the InfluxDB 3 system.queries table from a SQL query.");
        public override Task<CommandResult> KillQueryAsync(int pid) =>
            Unsupported<CommandResult>("KILL QUERY is an InfluxDB 1.x operation.");
        public override Task<IEnumerable<ContinuousQuery>> GetContinousQueriesAsync(string database) =>
            Unsupported<IEnumerable<ContinuousQuery>>("Continuous queries are not supported by InfluxDB 3.");
        public override Task<CommandResult> CreateContinuousQueryAsync(ContinuousQueryParams cqParams) =>
            Unsupported<CommandResult>("Continuous queries are not supported by InfluxDB 3.");
        public override Task<CommandResult> DropContinuousQueryAsync(string database, string cqName) =>
            Unsupported<CommandResult>("Continuous queries are not supported by InfluxDB 3.");
        public override Task<CommandResult> BackfillAsync(string database, BackfillParams backfillParams) =>
            Unsupported<CommandResult>("The legacy InfluxQL backfill builder is not supported for InfluxDB 3.");
        public override Task<ServerDiagnostics> GetDiagnosticsAsync() =>
            Unsupported<ServerDiagnostics>("The InfluxDB 1.x diagnostics endpoint is not available in InfluxDB 3.");
        public override Task<ServerStats> GetStatsAsync() =>
            Unsupported<ServerStats>("The InfluxDB 1.x statistics endpoint is not available in InfluxDB 3.");
        public override Task<IEnumerable<DatabaseUser>> GetUsersAsync() =>
            Unsupported<IEnumerable<DatabaseUser>>("InfluxDB 3 uses token-based authentication.");
        public override Task<CommandResult> CreateUserAsync(string username, string password, bool isAdmin) =>
            Unsupported<CommandResult>("InfluxDB 3 uses token-based authentication.");
        public override Task<CommandResult> DropUserAsync(string username) =>
            Unsupported<CommandResult>("InfluxDB 3 uses token-based authentication.");
        public override Task<CommandResult> SetPasswordAsync(string username, string password) =>
            Unsupported<CommandResult>("InfluxDB 3 uses token-based authentication.");
        public override Task<IEnumerable<DatabaseGrant>> GetPrivilegesAsync(string username) =>
            Unsupported<IEnumerable<DatabaseGrant>>("InfluxDB 3 uses token-based authentication.");
        public override Task<CommandResult> GrantAdministratorAsync(string username) =>
            Unsupported<CommandResult>("InfluxDB 3 uses token-based authentication.");
        public override Task<CommandResult> RevokeAdministratorAsync(string username) =>
            Unsupported<CommandResult>("InfluxDB 3 uses token-based authentication.");
        public override Task<CommandResult> GrantPrivilegeAsync(string username, DatabasePrivileges privilege, string database) =>
            Unsupported<CommandResult>("InfluxDB 3 uses token-based authentication.");
        public override Task<CommandResult> RevokePrivilegeAsync(string username, DatabasePrivileges privilege, string database) =>
            Unsupported<CommandResult>("InfluxDB 3 uses token-based authentication.");

        private async Task<IEnumerable<ResultSeries>> QuerySqlAsync(string database, string query,
            CancellationToken cancellationToken = default)
        {
            var columns = new List<string>();
            var values = new List<IList<object>>();
            await foreach (var batch in influx.QueryBatches(query, database: database).WithCancellation(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (columns.Count == 0)
                    columns.AddRange(batch.Schema.FieldsList.Select(field => field.Name));

                for (var rowIndex = 0; rowIndex < batch.Length; rowIndex++)
                {
                    var row = new List<object>();
                    for (var columnIndex = 0; columnIndex < batch.ColumnCount; columnIndex++)
                        row.Add(GetArrowValue(batch.Column(columnIndex), rowIndex));
                    values.Add(row);
                }
            }
            if (columns.Count == 0) return new List<ResultSeries>();
            return new[] { new ResultSeries(null, columns, new Dictionary<string, string>(), values) };
        }

        private async Task<IList<SchemaColumn>> GetSchemaColumnsAsync(string database, string measurement)
        {
            Require(measurement, "measurement");
            var query = "SELECT column_name, data_type FROM information_schema.columns "
                + "WHERE table_schema = 'iox' AND table_name = " + QuoteLiteral(measurement)
                + " ORDER BY ordinal_position";
            var series = await QuerySqlAsync(database, query);
            var result = new List<SchemaColumn>();
            foreach (var item in series)
            {
                var nameIndex = item.GetColumnIndex("column_name");
                var typeIndex = item.GetColumnIndex("data_type");
                foreach (var row in item.Values)
                    result.Add(new SchemaColumn(Convert.ToString(row[nameIndex]), Convert.ToString(row[typeIndex])));
            }
            return result;
        }

        private async Task<string> SendAsync(HttpMethod method, string path, string body = null, string mediaType = "application/json")
        {
            using (var request = new HttpRequestMessage(method, path))
            {
                if (body != null) request.Content = new StringContent(body, Encoding.UTF8, mediaType);
                using (var response = await adminHttp.SendAsync(request).ConfigureAwait(false))
                {
                    var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) throw CreateHttpException(response.StatusCode, responseBody);
                    return responseBody;
                }
            }
        }

        private async Task<CommandResult> SendForResponseAsync(
            HttpMethod method, string path, string body = null, string mediaType = "application/json")
        {
            using (var request = new HttpRequestMessage(method, path))
            {
                if (body != null) request.Content = new StringContent(body, Encoding.UTF8, mediaType);
                using (var response = await adminHttp.SendAsync(request).ConfigureAwait(false))
                {
                    var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) throw CreateHttpException(response.StatusCode, responseBody);
                    return new CommandResult(string.IsNullOrWhiteSpace(responseBody) ? "{}" : responseBody,
                        response.StatusCode, true);
                }
            }
        }

        private static IEnumerable<string> ColumnValues(IEnumerable<ResultSeries> series, string column)
        {
            foreach (var item in series)
            {
                var index = item.GetColumnIndex(column);
                if (index < 0) continue;
                foreach (var row in item.Values)
                {
                    var value = Convert.ToString(row[index], CultureInfo.InvariantCulture);
                    if (!string.IsNullOrWhiteSpace(value)) yield return value;
                }
            }
        }

        private static object GetArrowValue(IArrowArray array, int index)
        {
            if (array == null || array.IsNull(index)) return null;

            var type = array.GetType();
            if (type.Name.StartsWith("DictionaryArray", StringComparison.Ordinal))
            {
                var indices = (IArrowArray)type.GetProperty("Indices").GetValue(array);
                var dictionary = (IArrowArray)type.GetProperty("Dictionary").GetValue(array);
                var dictionaryIndex = Convert.ToInt32(GetArrowValue(indices, index), CultureInfo.InvariantCulture);
                return GetArrowValue(dictionary, dictionaryIndex);
            }

            foreach (var methodName in new[] { "GetValue", "GetString", "GetTimestamp", "GetDateTime" })
            {
                var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(int) }, null);
                if (method != null) return method.Invoke(array, new object[] { index });
            }

            return array.ToString();
        }

        private static string ToLineProtocol(DataPoint point)
        {
            if (point == null) throw new ArgumentNullException("point");
            if (point.Fields == null || point.Fields.Count == 0)
                throw new ArgumentException("An InfluxDB point must contain at least one field.", "point");

            var builder = new StringBuilder(EscapeKey(point.Measurement));
            if (point.Tags != null)
                foreach (var tag in point.Tags.OrderBy(item => item.Key))
                    builder.Append(',').Append(EscapeKey(tag.Key)).Append('=').Append(EscapeKey(Convert.ToString(tag.Value, CultureInfo.InvariantCulture)));

            builder.Append(' ');
            builder.Append(string.Join(",", point.Fields.OrderBy(item => item.Key)
                .Select(field => EscapeKey(field.Key) + "=" + FormatField(field.Value))));
            var utc = point.TimeStamp.Kind == DateTimeKind.Utc ? point.TimeStamp : point.TimeStamp.ToUniversalTime();
            var nanoseconds = (utc.Ticks - DateTime.UnixEpoch.Ticks) * 100L;
            builder.Append(' ').Append(nanoseconds.ToString(CultureInfo.InvariantCulture));
            return builder.ToString();
        }

        private static string FormatField(object value)
        {
            if (value == null) throw new ArgumentException("InfluxDB field values cannot be null.");
            if (value is string || value is char)
                return "\"" + Convert.ToString(value).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            if (value is bool boolean) return boolean ? "true" : "false";
            if (value is byte || value is sbyte || value is short || value is ushort || value is int || value is long)
                return Convert.ToString(value, CultureInfo.InvariantCulture) + "i";
            if (value is uint || value is ulong)
                return Convert.ToString(value, CultureInfo.InvariantCulture) + "u";
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string EscapeKey(string value) =>
            (value ?? string.Empty).Replace("\\", "\\\\").Replace(" ", "\\ ").Replace(",", "\\,").Replace("=", "\\=");
        private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        private static string QuoteLiteral(string value) => "'" + value.Replace("'", "''") + "'";
        private static bool IsTagType(string type) =>
            type != null && type.IndexOf("Dictionary", StringComparison.OrdinalIgnoreCase) >= 0;
        private static void Require(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(name);
        }
        private static Task<T> Unsupported<T>(string message) =>
            Task.FromException<T>(new NotSupportedException(message));
        private static Exception CreateHttpException(HttpStatusCode statusCode, string body) =>
            new HttpRequestException("InfluxDB 3 request failed (" + (int)statusCode + " " + statusCode + "): "
                + (string.IsNullOrWhiteSpace(body) ? "No response body." : body));
        private static CommandResult SuccessResponse() =>
            new CommandResult("{}", HttpStatusCode.NoContent, true);

        private sealed class SchemaColumn
        {
            public string Name { get; private set; }
            public string Type { get; private set; }
            public SchemaColumn(string name, string type) { Name = name; Type = type; }
        }
    }
}
