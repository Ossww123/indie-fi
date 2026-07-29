-- Where did the database's time go? Cumulative per-statement stats since the last
-- reset, top 25 by total execution time. This catches brief-but-frequent work
-- (e.g. failed INSERTs racing on the unique index) that instantaneous sampling misses.
--   calls     : how many times this statement ran
--   total_ms  : total execution time across all calls
--   mean_ms   : average per call
--   rows      : total rows returned/affected
SELECT
    calls,
    round(total_exec_time::numeric, 1)                 AS total_ms,
    round(mean_exec_time::numeric, 2)                  AS mean_ms,
    rows,
    left(regexp_replace(query, '\s+', ' ', 'g'), 75)   AS query
FROM pg_stat_statements
WHERE dbid = (SELECT oid FROM pg_database WHERE datname = 'indiefi')
ORDER BY total_exec_time DESC
LIMIT 25;
