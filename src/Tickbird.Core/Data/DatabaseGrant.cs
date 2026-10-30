using System;

namespace Tickbird.Core.Data
{
    /// <summary>
    /// InfluxDB server granted database privileges.
    /// </summary>
    public class DatabaseGrant
    {
        #region Fields

        /// <summary>
        /// The database name the granted privilege is for.
        /// </summary>
        public readonly string Database;

        /// <summary>
        /// The granted database privilege.
        /// </summary>
        public readonly DatabasePrivileges Privilege;

        #endregion Fields

        #region Constructors

        public DatabaseGrant(string database, DatabasePrivileges privilege = DatabasePrivileges.None)
        {
            if (string.IsNullOrWhiteSpace(database)) throw new ArgumentNullException("database");
            Database = database;
            Privilege = privilege;
        }

        #endregion Constructors
    }
}
