using Newtonsoft.Json;
using Tickbird.Core.Providers;

namespace Tickbird.Core.Data
{
    /// <summary>
    /// Represents the connection information required to connect to a specific InfluxDB server.
    /// </summary>
    public class DatabaseConnection
    {
        #region Fields

        #endregion Fields

        #region Properties

        /// <summary>
        /// Sets the unique ID of the connction instance. Ideally this value
        /// does not changed once assigned.
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Gets or sets the human-readable name of the connection.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Gets or sets the host URL for the InfluxDB server to connect to.
        /// </summary>
        public string Host { get; set; }

        /// <summary>
        /// Gets or sets the port number to use when connecting to the InfluxDB server.
        /// </summary>
        public ushort Port { get; set; }

        /// <summary>
        /// The name of the preferred database to use (optional).
        /// </summary>
        public string Database { get; set; }

        /// <summary>
        /// Gets or sets the username to use when connecting to the InfluxDB server.
        /// </summary>
        public string Username { get; set; }

        /// <summary>
        /// Gets or sets the password to use when connecting to the InfluxDB server.
        /// </summary>
        public string Password { get; set; }

        /// <summary>
        /// Gets or sets the bearer token used by InfluxDB 3.x.
        /// </summary>
        public string Token { get; set; }

        /// <summary>
        /// Gets or sets the optional group the connection belongs to (for example PROD, DEV).
        /// Null or blank means the connection is not in a group. Older settings files have no value.
        /// </summary>
        public string? Group { get; set; }

        /// <summary>
        /// Gets or sets the InfluxDB server generation.
        /// Existing saved connections default to InfluxDB 1.x.
        /// </summary>
        public InfluxDbServerVersion ServerVersion { get; set; }

        private DatabaseKind? kind;

        /// <summary>
        /// Which database this connection is for. Connections saved before other databases were supported have no
        /// kind and are InfluxDB 1.x or 3.x according to <see cref="ServerVersion"/>.
        /// </summary>
        public DatabaseKind Kind
        {
            get => kind ?? (ServerVersion == InfluxDbServerVersion.InfluxDb3 ? DatabaseKind.InfluxDb3 : DatabaseKind.InfluxDb1);
            set
            {
                kind = value;
                // Keep the old setting right for the InfluxDB kinds, so older files and code still read it correctly.
                if (value == DatabaseKind.InfluxDb3) ServerVersion = InfluxDbServerVersion.InfluxDb3;
                else if (value == DatabaseKind.InfluxDb1) ServerVersion = InfluxDbServerVersion.InfluxDb1;
            }
        }

        /// <summary>What the connection's database supports and how it is presented.</summary>
        [JsonIgnore]
        public DatabaseProfile Profile => DatabaseProfiles.For(this);

        /// <summary>
        /// Gets whether or not to use SSL to communicate with the InfluxDB server.
        /// </summary>
        public bool UseSsl { get; set; }

        /// <summary>
        /// Gets the combined/unified host connection string including protocol and port number for
        /// HTTP API access to the InfluxDB server.
        /// </summary>
        [JsonIgnore]
        public string HttpConnectionString
        {
            get
            {
                return string.Format("{0}://{1}:{2}", UseSsl ? "https" : "http", Host, Port);
            }
        }

        #endregion Properties

        #region Constructors

        /// <summary>
        /// Creates an InfluxDB connection with the provided configuration details.
        /// </summary>
        /// <param name="id">A unique ID to assign the connection.</param>
        /// <param name="name">The human-readable name for the connection.</param>
        /// <param name="host">The host URL for the InfluxDB server to connect to.</param>
        /// <param name="port">The port number to use when connecting to the InfluxDB server.</param>
        /// <param name="username">The username to use when connecting to the InfluxDB server.</param>
        /// <param name="password">The password to use when connecting to the InfluxDB server.</param>
        /// <param name="useSsl">Whether or not to use SSL to communicate with the InfluxDB server.</param>
        /// <param name="database">The name of the preferred database to use (optional).</param>
        public DatabaseConnection(string id, string name, string host, ushort port, 
            string username, string password, bool useSsl = true, string database = null,
            InfluxDbServerVersion serverVersion = InfluxDbServerVersion.InfluxDb1, string token = null)
        {
            Id = id;
            Name = name;
            Host = host;
            Port = port;
            Username = username;
            Password = password;
            UseSsl = useSsl;
            Database = database;
            ServerVersion = serverVersion;
            Token = token;
        }

        #endregion Constructors

        #region Methods

        #endregion Methods
    }
}
