using System;
using Tickbird.Core.Providers;

namespace Tickbird.Core.Data
{
    /// <summary>
    /// Factory class for creating <see cref="DatabaseClient">InfluxDB clients</see>.
    /// </summary>
    public static class DatabaseClientFactory
    {
        #region Fields

        /// <summary>
        /// Supplies the "allow untrusted TLS certificates" preference. The host application sets this
        /// so the data layer does not depend on any UI framework.
        /// </summary>
        public static Func<bool> AllowUntrustedSsl { get; set; } = () => false;

        #endregion Fields

        #region Properties

        #endregion Properties

        #region Constructors

        #endregion Constructors

        #region Methods

        /// <summary>
        /// Creates a new <see cref="DatabaseClient">InfluxDB client</see> using the default client implementation.
        /// </summary>
        /// <param name="connection">The connection information to use when creating the client.</param>
        /// <returns>An InfluxDB client that can be used to communicate with the InfluxDB server API.</returns>
        public static DatabaseClient Create(DatabaseConnection connection)
        {
            if (connection == null) throw new ArgumentNullException("connection");

            return DatabaseProfiles.For(connection).CreateClient(connection);
        }

        #endregion Methods
    }
}
