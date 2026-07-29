-- Per-backend detail: who (pid), in what state, waiting on what (wait_event),
-- and blocked by whom (blocked_by). Read-only: takes no locks.
--
-- Expected evidence (hypothesis):
--   several rows with wait_event = 'transactionid' whose blocked_by all point to the
--   same single leader pid = the picture of "everyone queued behind the same unique
--   index entry (same key_id), waiting for the leader to commit".
SELECT
    to_char(clock_timestamp(), 'HH24:MI:SS.MS')            AS ts,
    pid,
    state,
    wait_event_type                                        AS wtype,
    wait_event                                             AS wevent,
    pg_blocking_pids(pid)                                  AS blocked_by,  -- pids blocking this one
    left(regexp_replace(query, '\s+', ' ', 'g'), 55)       AS query        -- head of query, whitespace folded
FROM pg_stat_activity
WHERE datname = 'indiefi'
  AND backend_type = 'client backend'
  AND pid <> pg_backend_pid()
ORDER BY (wait_event_type IS NULL), wait_event_type, pid;  -- waiting ones first
