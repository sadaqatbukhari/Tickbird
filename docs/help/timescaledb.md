# TimescaleDB help

This guide covers what the [README](../../README.md#working-with-timescaledb) does not: starting a server, a query cookbook,
how each Tickbird tool behaves on TimescaleDB, what your account needs, and what to do when something fails. The examples
were run against TimescaleDB 2.31 on PostgreSQL 16 with the sample data in [`docs/sample-data`](../sample-data).

## 1. Start a server to try

```bash
docker run -d --name tickbird-timescale -p 5432:5432 -e POSTGRES_PASSWORD=secret timescale/timescaledb:latest-pg16
```

In **Manage Connections** choose **TimescaleDB**, host `localhost`, port `5432`, user `postgres`, password `secret`.
Leave **Database** empty to list every database, or type one to show only that.

The sample data (`docs/sample-data/timescaledb.sql`) creates the database `metrics` with the hypertables `cpu`, `mem` and
`gps` and the continuous aggregate `cpu_5m`:

```bash
docker cp docs/sample-data/timescaledb.sql tickbird-timescale:/tmp/seed.sql
docker exec tickbird-timescale psql -U postgres -v ON_ERROR_STOP=1 -q -f /tmp/seed.sql
```

## 2. What you see in the tree

| Tree item | Is |
| --- | --- |
| Connection | The server |
| Database | A database you can connect to (templates are not listed) |
| Table | A table, hypertable, view or materialized view, including continuous aggregates. Tables outside `public` show as `schema.table`. The server's own schemas (`pg_catalog`, `information_schema`, `_timescaledb_*`, `timescaledb_*`) are hidden. |

Right-click a table for **New Query**, **Show Text Columns**, **Show Column Values**, **Show Columns**, **Show Chunks**,
**Truncate Table** and **Drop Table**.

## 3. Create a hypertable

Run this in a query tab on the database:

```sql
CREATE TABLE sensor (time timestamptz NOT NULL, device text, temperature double precision);
SELECT create_hypertable('sensor', 'time', chunk_time_interval => INTERVAL '1 day');
```

Press **Refresh** on the database to see it. A *tag* is any text column (`device`) and a *field* is any other column. The time
column shows under **Show Columns** like the rest. See [What is a tag](#what-is-a-tag-here) below.

## 4. Query cookbook

Open a query tab on a table (double-click it) and edit. Results can be shown as a grid, chart or map. Timestamps are shown in UTC.

**Average per 5 minutes, per host**

```sql
SELECT time_bucket('5 minutes', time) AS bucket, host, avg(usage_user) AS avg_user
FROM cpu
WHERE time > now() - interval '1 hour'
GROUP BY 1, 2
ORDER BY 1 DESC, 2
```

**First and last value per host**

```sql
SELECT host, last(usage_user, time) AS latest, first(usage_user, time) AS earliest
FROM cpu GROUP BY host ORDER BY host
```

**95th percentile**

```sql
SELECT host, percentile_cont(0.95) WITHIN GROUP (ORDER BY usage_user) AS p95
FROM cpu GROUP BY host ORDER BY host
```

**Fill gaps** (`time_bucket_gapfill` needs both a lower and an upper time bound in `WHERE`)

```sql
SELECT time_bucket_gapfill('1 minute', time) AS minute, host, locf(avg(usage_user)) AS usage
FROM cpu
WHERE host = 'web-1' AND time > now() - interval '10 minutes' AND time <= now()
GROUP BY 1, 2
ORDER BY 1
```

`locf()` carries the last value forward; use `interpolate()` to draw a straight line between known points.

**Draw a track on the map** (any result with latitude and longitude columns)

```sql
SELECT time, latitude, longitude, state, speed FROM gps ORDER BY time
```

Switch the result to **Map** and colour by `state`.

**Read a continuous aggregate** like any table

```sql
SELECT * FROM cpu_5m ORDER BY time DESC LIMIT 20
```

**Size and rows of a hypertable**

```sql
SELECT approximate_row_count('cpu'), pg_size_pretty(hypertable_size('cpu'))
```

`approximate_row_count()` is instant on a large table; `count(*)` is not.

## 5. The tools

### Show Chunks
A hypertable is stored as chunks, one per time range. **Show Chunks** lists them as
`table,chunk=_hyper_1_3_chunk,range_start=...,range_end=...,compressed=false`, newest first (up to 1,000). A plain table has
no chunks, so it lists the distinct combinations of its text columns instead.

### Truncate Table
Runs `TRUNCATE TABLE` and keeps the table. It cannot be undone and it asks you to confirm by typing the name.

### Retention policies (Show Retention Policies)
One policy per hypertable. **Create** asks for a table and a duration (`30d`, `12h`, `1w`, or PostgreSQL text such as
`30 days`). Creating a policy for a table that already has one replaces it, and **Edit** does the same. The list shows the
table, how long data is kept and the table's chunk interval. Under the hood this is `add_retention_policy` and
`remove_retention_policy`; chunks older than the duration are dropped by TimescaleDB's background job, not at once.

### Continuous aggregates (Show Continuous Aggregates)
**Create** asks for a name, a source table, a bucket interval, group-by text columns and aggregate expressions such as
`avg(usage_user) AS usage_user`. It creates the view `WITH NO DATA` and then a refresh policy: it refreshes every
*interval*, over the window from 3 × *interval* ago to 1 × *interval* ago. To fill history, run once in a query tab:

```sql
CALL refresh_continuous_aggregate('cpu_5m', NULL, NULL);
```

The list shows each aggregate as the `CREATE MATERIALIZED VIEW` it stands for. **Drop** removes the view.

### Backfill (Run Backfill)
Downsamples existing data of a time range (UTC) into a destination table. The table is created if missing, otherwise rows are
added, so running the same range twice duplicates rows. Optional filters are SQL conditions on the source, for example
`host = 'web-1'`.

### Users and Privileges
Users are PostgreSQL **roles that can log in**; **Administrator** means superuser. **Grant** gives a role *Read*
(`SELECT`), *Write* (`INSERT, UPDATE, DELETE`) or *All* on every table in the database's user schemas, now and for tables made
later. Granting again replaces the earlier right, so **Edit Privilege** can lower it and **Revoke** removes it. The list shows
a level for a database only when the role has that right on **every** table there, so a role with rights on some tables
shows nothing. Superusers show no privileges because they need none.

### Show Queries, Statistics, Diagnostics
- **Show Queries** lists active sessions of other clients (`pg_stat_activity`); **Kill Query** cancels the statement and
  leaves the session connected.
- **Statistics** shows databases, tables, sessions, hypertables and background jobs.
- **Diagnostics** shows the PostgreSQL and TimescaleDB versions, uptime, connections and licence. **Ping** in the connection
  dialog shows `2.31.0 on PostgreSQL 16.x`, or notes that the extension is not installed.

### Create and drop a database
**Create Database** makes the database and runs `CREATE EXTENSION IF NOT EXISTS timescaledb` in it. If your account may not
install extensions, the database is still created and the message says so. **Drop Database** first ends other sessions on it.
`postgres`, `template0` and `template1` cannot be dropped from the tree.

## 6. What your account needs

| To do | The role needs |
| --- | --- |
| Browse tables and columns | Connect to the database and `SELECT` |
| Create a database | `CREATEDB` |
| Install the extension | Superuser, or the extension marked trusted |
| Users and privileges | `CREATEROLE` (or superuser) |
| Show Queries of other users | `pg_read_all_stats` or superuser; otherwise their query text is hidden |

If your account cannot connect to the `postgres` database (some cloud services), type the database to use in **Database**.

## 7. What is a tag here?

TimescaleDB has no tags. Tickbird shows every **text** column as a tag and every other column as a field, so the same views work
for all databases. It is only a convenience: a free-text column such as a message is shown as a tag too, and a numeric
`device_id` is a field. In SQL, group by whatever you like.

## 8. Troubleshooting

| You see | Cause and fix |
| --- | --- |
| Ping: `... (TimescaleDB extension not installed)` | Run `CREATE EXTENSION timescaledb;` in that database as a superuser. |
| Hypertable, chunk and policy tools list nothing | The database has no hypertables, or the extension is missing there. |
| `columnstore not enabled on hypertable` | Compression needs it first: `ALTER TABLE cpu SET (timescaledb.compress, timescaledb.compress_segmentby = 'host');` and then `SELECT add_compression_policy('cpu', INTERVAL '7 days');`. |
| Gapfill returns no rows | `time_bucket_gapfill` needs a lower **and** an upper bound on `time` in `WHERE`, and data inside that window. |
| `permission denied` when granting | Your role is not allowed to grant on that schema. Connect as the owner or a superuser. |
| Create Database says the extension could not be installed | Create the database anyway and ask an administrator to install `timescaledb` in it. |
| Test connection fails with a timeout | Check host and port. A local PostgreSQL may already own port 5432; map the container to another port (`-p 5433:5432`). |
| SSL error | Turn **Use SSL** off for a local server, or allow untrusted certificates in Settings for a self-signed one. |
