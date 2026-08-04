-- One-line snapshot of who is queueing during the burst. Read-only: takes no locks.
--   ts           : when this sample was taken
--   active       : backends currently running something (waiting counts as active)
--   lock_waiters : of those, how many are stalled waiting for a lock
--   on_xid       : lock_waiters waiting for another transaction (transactionid) to finish
--                  -- this is what shows up when inserting a duplicate value into a
--                     unique index and blocking on the leader transaction
--   on_tuple     : how many are waiting on a specific row (tuple) lock
SELECT
    to_char(clock_timestamp(), 'HH24:MI:SS.MS')            AS ts,
    count(*) FILTER (WHERE state = 'active')               AS active,
    count(*) FILTER (WHERE wait_event_type = 'Lock')       AS lock_waiters,
    count(*) FILTER (WHERE wait_event = 'transactionid')   AS on_xid,
    count(*) FILTER (WHERE wait_event = 'tuple')           AS on_tuple
FROM pg_stat_activity
WHERE datname = 'indiefi'
  AND backend_type = 'client backend'   -- only app/seed connections (skip background procs)
  AND pid <> pg_backend_pid();          -- do not count this observer query itself
