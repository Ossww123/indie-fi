-- Clear accumulated statement stats so the next load run starts from zero.
-- Run this right before firing the load.
SELECT pg_stat_statements_reset();
