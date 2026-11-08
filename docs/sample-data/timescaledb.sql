SELECT 'CREATE DATABASE metrics' WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'metrics')\gexec
\c metrics
CREATE EXTENSION IF NOT EXISTS timescaledb;

DROP TABLE IF EXISTS cpu, mem, gps CASCADE;

CREATE TABLE cpu (time timestamptz NOT NULL, host text, region text, usage_user double precision, usage_system double precision);
CREATE TABLE mem (time timestamptz NOT NULL, host text, region text, used_percent double precision, free_mb bigint);
CREATE TABLE gps (time timestamptz NOT NULL, vehicle text, latitude double precision, longitude double precision, speed double precision, state text);
SELECT create_hypertable('cpu', 'time', chunk_time_interval => INTERVAL '6 hours');
SELECT create_hypertable('mem', 'time', chunk_time_interval => INTERVAL '6 hours');
SELECT create_hypertable('gps', 'time', chunk_time_interval => INTERVAL '6 hours');

-- 24 hours, one point a minute, three hosts
INSERT INTO cpu
SELECT t, h.host, h.region,
       round((30 + 25 * sin(extract(epoch FROM t) / 3600.0 + h.n) + random() * 8)::numeric, 2),
       round((8 + 4 * sin(extract(epoch FROM t) / 1800.0 + h.n) + random() * 2)::numeric, 2)
FROM generate_series(now() - INTERVAL '24 hours', now(), INTERVAL '1 minute') AS t,
     (VALUES (1, 'web-1', 'eu'), (2, 'web-2', 'eu'), (3, 'web-3', 'us')) AS h(n, host, region);

INSERT INTO mem
SELECT t, h.host, h.region,
       round((55 + 15 * sin(extract(epoch FROM t) / 7200.0 + h.n) + random() * 3)::numeric, 2),
       (4096 - (55 + 15 * sin(extract(epoch FROM t) / 7200.0 + h.n)) * 40)::bigint
FROM generate_series(now() - INTERVAL '24 hours', now(), INTERVAL '1 minute') AS t,
     (VALUES (1, 'web-1', 'eu'), (2, 'web-2', 'eu'), (3, 'web-3', 'us')) AS h(n, host, region);

-- a harvester driving laps around a field near Warsaw, working then turning
INSERT INTO gps
SELECT t, 'harvester-1',
       round((52.2300 + 0.004 * sin(n / 12.0) + n * 0.00004)::numeric, 5),
       round((21.0100 + 0.006 * cos(n / 12.0) + n * 0.00008)::numeric, 5),
       round((2 + CASE WHEN n % 30 < 20 THEN 3 ELSE 0 END + abs(sin(n / 7.0)) * 2)::numeric, 1),
       CASE WHEN n % 30 < 20 THEN 'work' ELSE 'turn' END
FROM (SELECT t, row_number() OVER (ORDER BY t) AS n FROM generate_series(now() - INTERVAL '3 hours', now(), INTERVAL '5 seconds') AS t) s;

CREATE MATERIALIZED VIEW cpu_5m WITH (timescaledb.continuous) AS
SELECT time_bucket(INTERVAL '5 minutes', time) AS time, host, avg(usage_user) AS usage_user
FROM cpu GROUP BY 1, host WITH NO DATA;
CALL refresh_continuous_aggregate('cpu_5m', NULL, NULL);

SELECT 'cpu' AS tbl, count(*) FROM cpu UNION ALL SELECT 'mem', count(*) FROM mem UNION ALL SELECT 'gps', count(*) FROM gps UNION ALL SELECT 'cpu_5m', count(*) FROM cpu_5m;
