# CrateDB help

This guide covers what the [README](../../README.md#working-with-cratedb) does not: starting a server, a query cookbook, how
each Tickbird tool behaves on CrateDB, what is different from InfluxDB and TimescaleDB, and what to do when something fails.
The examples were run against CrateDB (`crate:latest`) with the sample data in [`docs/sample-data`](../sample-data).

## 1. Start a server to try

```bash
docker run -d --name tickbird-crate -p 5432:5432 -p 4200:4200 crate:latest -Cdiscovery.type=single-node
```

CrateDB listens on two ports:

| Port | Protocol | Use |
| --- | --- | --- |
| **5432** | PostgreSQL | **Tickbird connects here** |
| 4200 | HTTP | The Admin UI at `http://localhost:4200`. Tickbird refuses this port in the dialog. |

In **Manage Connections** choose **CrateDB**, host `localhost`, port `5432`. The user starts as `crate`; a new CrateDB has
that user with no password. There is no Database field: a CrateDB server has one database, `crate`, and tables live in schemas.

The sample data (`docs/sample-data/cratedb.sh`) creates `doc.cpu`, `doc.mem`, `doc.gps` and the partitioned `doc.events`:

```bash
bash docs/sample-data/cratedb.sh
```

## 2. What you see in the tree

| Tree item | Is |
| --- | --- |
| Connection | The cluster |
| `crate` | The one database. It cannot be created or dropped. |
| Table | A table or view. Tables in the `doc` schema show by name, the others as `schema.table`. `sys`, `information_schema`, `pg_catalog` and `blob` are hidden. |

Right-click a table for **New Query**, **Show Text Columns**, **Show Column Values**, **Show Columns**, **Show Partitions**,
**Delete All Rows** and **Drop Table**. The toolbar hides what CrateDB cannot do (retention policies, continuous queries,
backfill, creating and dropping a database).

## 3. Create a table

Run this in a query tab:

```sql
CREATE TABLE doc.sensor (
  time timestamp with time zone,
  device text,
  temperature double precision
);
```

CrateDB tables are **strict** by default: writing a column that does not exist fails. Add columns with `ALTER TABLE ... ADD COLUMN`
first, or create the table with `WITH (column_policy = 'dynamic')`. Writes become visible to queries after a refresh, about a
second later; run `REFRESH TABLE doc.sensor` to see them at once.

**Partition by day** (a generated column keeps the partition key out of your inserts):

```sql
CREATE TABLE doc.events (
  time timestamp with time zone, host text, level text, message text,
  day timestamp with time zone GENERATED ALWAYS AS date_trunc('day', time)
) PARTITIONED BY (day);
```

## 4. Query cookbook

Timestamps are shown in UTC. Results can be shown as a grid, chart or map.

**Average per 5 minutes, per host** (`date_bin` takes an interval, a timestamp and an origin)

```sql
SELECT date_bin('5 minutes'::interval, time, 0) AS bucket, host, avg(usage_user) AS avg_user
FROM doc.cpu
WHERE time > now() - INTERVAL '1 hour'
GROUP BY 1, 2
ORDER BY 1 DESC, 2
```

**Average per hour** with `date_trunc`

```sql
SELECT date_trunc('hour', time) AS hour, host, avg(usage_user) FROM doc.cpu GROUP BY 1, 2 ORDER BY 1 DESC
```

**Latest and earliest value per host** (CrateDB has `max_by` and `min_by` in place of `last` and `first`)

```sql
SELECT host, max_by(usage_user, time) AS latest, min_by(usage_user, time) AS earliest
FROM doc.cpu GROUP BY host ORDER BY host
```

**95th percentile**

```sql
SELECT host, percentile(usage_user, 0.95) AS p95 FROM doc.cpu GROUP BY host ORDER BY host
```

**Draw a track on the map**

```sql
SELECT time, latitude, longitude, state, speed FROM doc.gps ORDER BY time
```

**See how a table is defined**

```sql
SHOW CREATE TABLE doc.cpu
```

**Size, rows and health**

```sql
SELECT table_name, sum(num_docs) AS docs, sum(size) AS bytes
FROM sys.shards WHERE "primary" AND schema_name = 'doc' GROUP BY table_name ORDER BY 1;

SELECT table_name, health, severity FROM sys.health ORDER BY severity DESC;
```

`"primary"` is quoted because it is a keyword.

**Merge segments after many writes**

```sql
OPTIMIZE TABLE doc.cpu
```

## 5. The tools

### Show Partitions
Lists the partitions of a partitioned table as `events,partition=04732dpp6cs36dho60o30c1g,values={day=...}`, in partition
order (up to 1,000). A table that is not partitioned lists the distinct combinations of its text columns instead.

### Delete All Rows
Runs `DELETE FROM` on the table and keeps it (CrateDB has no `TRUNCATE`). It cannot be undone. On a large table this is slow;
dropping and recreating the table, or dropping a partition, is faster.

### Users and Privileges
Users are CrateDB users; the list marks superusers. **Create User** and **Password** need a non-blank password. **Create User** has no
Administrator option, because a superuser cannot be created with SQL.
CrateDB privileges are `DQL` (read), `DML` (write) and `DDL` (schema changes). Tickbird maps them like this:

| Tickbird | CrateDB | Statements |
| --- | --- | --- |
| Read | `DQL` | `GRANT DQL TO "user"` |
| Write | `DML` | `GRANT DML TO "user"` |
| All | `DQL, DML, DDL` | `GRANT DQL, DML, DDL TO "user"` |

Rights apply to the **whole cluster**, and the privilege list shows the one database `crate`. Granting replaces the earlier
rights (it revokes first), so **Edit Privilege** can lower one. A user who has `DQL` and `DML` shows as **All**.

> Rights granted on a single schema or table (`GRANT DQL ON SCHEMA doc TO "user"`) are not shown in the list, because Tickbird
> reads only cluster-level rights. Check those with
> `SELECT grantee, class, ident, type, state FROM sys.privileges`.

### Show Queries
Lists running jobs from `sys.jobs`, oldest first, without the listing query itself. **Kill Query** runs `KILL` on the job.
Refresh the list first: a job that has finished cannot be killed and Tickbird says so.

### Statistics
Four tables: **Nodes** (load, heap and disk per node), **Tables** (shards, replicas, partitioning), **Shards** (rows and bytes per
table) and **Health** (per table, with missing and under-replicated shards).

### Diagnostics
The node, host name, version and build, current time, **host uptime**, processors, maximum heap, cluster name, master node,
node count and the signed-in user. CrateDB does not report when a node started, so the uptime shown is that of the host the
node runs on. **Ping** in the connection dialog shows `CrateDB 5.x.y`.

### Writing data
Tickbird writes with parameterised `INSERT` statements, one per point, and a batch stops at the first failure (CrateDB takes one
statement per command, so a batch is not atomic). Values written as text fit numeric and timestamp columns.

## 6. Differences to keep in mind

| | CrateDB | In Tickbird |
| --- | --- | --- |
| Databases | One (`crate`) with schemas | The tree has one database node |
| Retention, continuous queries, backfill | Not built in | Buttons hidden |
| Clearing a table | `DELETE FROM` | **Delete All Rows** |
| Several statements in one command | Not accepted | Tickbird runs them one by one |
| Superusers | Not created with SQL | No Administrator option |
| Writes visible | After a refresh (about 1 s) | Use `REFRESH TABLE` to see them at once |
| Time functions | `date_bin`, `date_trunc`, `now()` | In the IntelliSense list |

## 7. What is a tag here?

CrateDB has no tags. Tickbird shows every **text** and **IP** column as a tag and every other column (numbers, timestamps,
objects) as a field, so the same views work for all databases. It is a convenience only; in SQL, group by whatever you like.

## 8. Troubleshooting

| You see | Cause and fix |
| --- | --- |
| `Port 4200 is CrateDB's HTTP port...` | Use the PostgreSQL port, `5432` unless you mapped it differently. |
| Connection refused or a timeout | The container is not running or the port is not published. Check `docker ps`; a local PostgreSQL may already own 5432, so publish another port (`-p 5434:5432`) and enter it in the dialog. |
| An authentication error | Enter the user's password. A fresh container accepts the user `crate` with no password. |
| An error about an unknown column when writing | The table is strict. Add the column first or create the table with `column_policy = 'dynamic'`. |
| A table is missing from the tree | Press **Refresh**. Tables in `sys`, `information_schema`, `pg_catalog` and `blob` are hidden on purpose. |
| Just-written rows are not found | Wait a second or run `REFRESH TABLE <table>`. |
| Show Queries is empty | No other jobs are running. The listing hides its own query. |
| A user has rights but the list is empty | The rights are on a schema or table, not the cluster. See the note under Users and Privileges. |
