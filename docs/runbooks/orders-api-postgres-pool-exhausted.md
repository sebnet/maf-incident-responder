# orders-api: Postgres connection pool exhausted

Owner: orders team. Last reviewed: 2026-11.

orders-api talks to Postgres (`ordersdb`) through an Npgsql pool: **max 20 connections, 5 s connection
timeout**. When every connection is taken, new requests wait up to 5 s for a free one and then fail. Postgres
itself is usually fine — the problem is who is holding the connections.

## Signals

- Alert: orders-api error rate > 5% (sometimes the latency alert fires first).
- orders-api error logs: `NpgsqlException` "The connection pool has been exhausted, either raise 'Max Pool Size'
  ... or 'Timeout' ...". Both `GET /orders` and `POST /orders` affected.
- Failing requests take ~5 s (the pool timeout), then 500. Some requests still succeed: failures are intermittent.
- catalog-api healthy.

## How to confirm

1. Pool saturation (1.0 = exhausted):
   ```promql
   sum by (service_name) (db_client_connection_count{db_client_connection_state="used"})
     / sum by (service_name) (db_client_connection_max)
   ```
   Pinned at or near 1.0 = confirmed.
2. Who is holding the connections? Look at the `postgresql` spans of orders-api in Tempo, sorted by duration.
   Normal queries take milliseconds; anything taking tens of seconds is the suspect. Read `db.query.text`.
3. If you have DB access: `select pid, state, now() - query_start as age, query from pg_stat_activity
   where datname = 'ordersdb' order by age desc;`
4. Check orders-api logs (info/warn, not only errors) around the start of the incident: background jobs log
   when they start. A job starting right before the errors is a strong hint.

Usual suspects, in our experience: a long-running query or background job holding connections, a
connection leak after a deploy (connections not disposed), a traffic spike bigger than the pool.

## Mitigation

- A job or query is holding connections: stop it (or cancel the backend with `pg_cancel_backend(pid)`).
  The pool recovers by itself within seconds once connections are returned.
- Can't stop it cleanly: restart orders-api. This drops all its connections and stops in-process background work.
- Leak after a deploy: roll back.
- **Don't** just raise Max Pool Size. It hides the problem and moves the pressure to Postgres
  (`max_connections`). Only as a temporary measure, and tell the orders team.

## Escalate to

- Orders team (#team-orders) — owns the background jobs and the pool settings.
- DBA on-call only if Postgres itself is unhealthy (high CPU, locks, `max_connections` reached).
