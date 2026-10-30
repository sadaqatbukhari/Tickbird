using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Tickbird.Core.Data
{
    /// <summary>
    /// Represents a collection of InfluxDB server statistics.
    /// </summary>
    public class ServerStats
    {
        #region Fields

        #endregion Fields

        #region Properties

        /// <summary>
        /// A list of server stats related to Continuous Queries.
        /// </summary>
        public IEnumerable<ResultSeries> CQ { get; set; }

        /// <summary>
        /// A list of server stats related to the database engine.
        /// </summary>
        public IEnumerable<ResultSeries> Engine { get; set; }

        /// <summary>
        /// A list of server stats related to shards.
        /// </summary>
        public IEnumerable<ResultSeries> Shard { get; set; }

        /// <summary>
        /// A list of server stats related to the HTTP process.
        /// </summary>
        public IEnumerable<ResultSeries> Httpd { get; set; }

        /// <summary>
        /// A list of server stats related to WAL storage.
        /// </summary>
        public IEnumerable<ResultSeries> WAL { get; set; }

        /// <summary>
        /// A list of server stats related to database writes.
        /// </summary>
        public IEnumerable<ResultSeries> Write { get; set; }

        /// <summary>
        /// A list of server stats related to the GoLang runtime.
        /// </summary>
        public IEnumerable<ResultSeries> Runtime { get; set; }

        /// <summary>
        /// A list of server stats related to databases.
        /// </summary>
        public IEnumerable<ResultSeries> Database { get; set; }

        /// <summary>
        /// A list of server stats related to the query executor.
        /// </summary>
        public IEnumerable<ResultSeries> QueryExecutor { get; set; }

        /// <summary>
        /// A list of server stats related to subscriptions.
        /// </summary>
        public IEnumerable<ResultSeries> Subscriber { get; set; }

        /// <summary>
        /// A list of server stats related to the TSM1 cache.
        /// </summary>
        public IEnumerable<ResultSeries> Tsm1Cache { get; set; }

        /// <summary>
        /// A list of server stats related to the TSM1 filestore.
        /// </summary>
        public IEnumerable<ResultSeries> Tsm1Filestore { get; set; }

        /// <summary>
        /// A list of server stats related to the TSM1 WAL storage.
        /// </summary>
        public IEnumerable<ResultSeries> Tsm1Wal { get; set; }

        /// <summary>Statistics of other databases, by name. The statistics tab lists these next to the InfluxDB categories.</summary>
        public IDictionary<string, IEnumerable<ResultSeries>> Custom { get; } = new Dictionary<string, IEnumerable<ResultSeries>>();

        #endregion Properties

        #region Constructors

        #endregion Constructors

        #region Methods

        #endregion Methods
    }
}
