#!/bin/bash
# Seeds CrateDB through its HTTP endpoint (CrateDB takes one statement at a time).
run() {
  out=$(curl -s -H 'Content-Type: application/json' http://localhost:4200/_sql -d "$(python -c 'import json,sys; print(json.dumps({"stmt": sys.argv[1]}))' "$1")")
  case "$out" in *'"error"'*) echo "FAILED: ${1:0:90}"; echo "$out"; exit 1;; esac
}

for t in cpu mem gps events; do run "DROP TABLE IF EXISTS doc.$t"; done
run "CREATE TABLE doc.cpu (time timestamp with time zone, host text, region text, usage_user double precision, usage_system double precision)"
run "CREATE TABLE doc.mem (time timestamp with time zone, host text, region text, used_percent double precision, free_mb bigint)"
run "CREATE TABLE doc.gps (time timestamp with time zone, vehicle text, latitude double precision, longitude double precision, speed double precision, state text)"
run "CREATE TABLE doc.events (time timestamp with time zone, host text, level text, message text, day timestamp with time zone GENERATED ALWAYS AS date_trunc('day', time)) PARTITIONED BY (day)"

# 24 hours, one point a minute, three hosts
run "INSERT INTO doc.cpu (time, host, region, usage_user, usage_system)
SELECT t, host, region,
       round(30 + 25 * sin(extract(epoch FROM t) / 3600.0 + n) + random() * 8, 2),
       round(8 + 4 * sin(extract(epoch FROM t) / 1800.0 + n) + random() * 2, 2)
FROM (SELECT date_trunc('minute', now()) - m * INTERVAL '1 minute' AS t, h.n, h.host, h.region
      FROM generate_series(0, 1440) AS g(m), unnest([1, 2, 3], ['web-1', 'web-2', 'web-3'], ['eu', 'eu', 'us']) AS h(n, host, region)) s"

run "INSERT INTO doc.mem (time, host, region, used_percent, free_mb)
SELECT t, host, region,
       round(55 + 15 * sin(extract(epoch FROM t) / 7200.0 + n) + random() * 3, 2),
       CAST(4096 - (55 + 15 * sin(extract(epoch FROM t) / 7200.0 + n)) * 40 AS bigint)
FROM (SELECT date_trunc('minute', now()) - m * INTERVAL '1 minute' AS t, h.n, h.host, h.region
      FROM generate_series(0, 1440) AS g(m), unnest([1, 2, 3], ['web-1', 'web-2', 'web-3'], ['eu', 'eu', 'us']) AS h(n, host, region)) s"

# a harvester driving laps around a field near Warsaw, working then turning (every 5 seconds for 3 hours)
run "INSERT INTO doc.gps (time, vehicle, latitude, longitude, speed, state)
SELECT date_trunc('second', now()) - n * INTERVAL '5 seconds', 'harvester-1',
       round(52.2300 + 0.004 * sin(n / 12.0) + n * 0.00004, 5),
       round(21.0100 + 0.006 * cos(n / 12.0) + n * 0.00008, 5),
       round(2 + CASE WHEN n % 30 < 20 THEN 3 ELSE 0 END + abs(sin(n / 7.0)) * 2, 1),
       CASE WHEN n % 30 < 20 THEN 'work' ELSE 'turn' END
FROM generate_series(0, 2160) AS g(n)"

# a few days of log events, so the table has partitions (one per day)
run "INSERT INTO doc.events (time, host, level, message)
SELECT date_trunc('minute', now()) - (d * 24 * 60 + m * 7) * INTERVAL '1 minute', host,
       CASE WHEN (d + m) % 11 = 0 THEN 'error' WHEN (d + m) % 5 = 0 THEN 'warn' ELSE 'info' END,
       'request ' || m || ' handled'
FROM generate_series(0, 3) AS g1(d), generate_series(0, 120) AS g2(m), unnest(['web-1', 'web-2', 'web-3']) AS h(host)"

for t in cpu mem gps events; do run "REFRESH TABLE doc.$t"; done
for t in cpu mem gps events; do
  curl -s -H 'Content-Type: application/json' http://localhost:4200/_sql -d "{\"stmt\":\"SELECT '$t' AS tbl, count(*) FROM doc.$t\"}" | python -c 'import json,sys; r=json.load(sys.stdin)["rows"][0]; print(r[0], r[1])'
done
