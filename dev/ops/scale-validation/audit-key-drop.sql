\set ON_ERROR_STOP on
SET statement_timeout = '30s';
SET default_transaction_read_only = on;

SELECT 'drop.exists', count(*)::text
  FROM key_drops
 WHERE id = :drop_id;

SELECT 'drop.total_keys', coalesce(max(total_keys), 0)::text
  FROM key_drops
 WHERE id = :drop_id;

SELECT 'drop.state_version', coalesce(max(state_version), 0)::text
  FROM key_drops
 WHERE id = :drop_id;

SELECT 'inventory.keys', count(*)::text
  FROM game_keys
 WHERE drop_id = :drop_id;

SELECT 'grants.total', count(*)::text
  FROM key_grants
 WHERE drop_id = :drop_id;

SELECT 'grants.distinct_users', count(DISTINCT user_id)::text
  FROM key_grants
 WHERE drop_id = :drop_id;

SELECT 'grants.distinct_keys', count(DISTINCT key_id)::text
  FROM key_grants
 WHERE drop_id = :drop_id;

SELECT 'grants.duplicate_user_groups', count(*)::text
  FROM (
        SELECT user_id
          FROM key_grants
         WHERE drop_id = :drop_id
         GROUP BY user_id
        HAVING count(*) > 1
       ) AS duplicates;

SELECT 'grants.duplicate_key_groups', count(*)::text
  FROM (
        SELECT key_id
          FROM key_grants
         WHERE drop_id = :drop_id
         GROUP BY key_id
        HAVING count(*) > 1
       ) AS duplicates;

SELECT 'inventory.remaining',
       (SELECT count(*) FROM game_keys WHERE drop_id = :drop_id)
       - (SELECT count(*) FROM key_grants WHERE drop_id = :drop_id);

SELECT 'invariant.inventory_total_mismatch',
       CASE WHEN
         (SELECT count(*) FROM game_keys WHERE drop_id = :drop_id)
         <> (SELECT total_keys FROM key_drops WHERE id = :drop_id)
       THEN '1' ELSE '0' END;

SELECT 'invariant.grants_over_inventory',
       CASE WHEN
         (SELECT count(*) FROM key_grants WHERE drop_id = :drop_id)
         > (SELECT count(*) FROM game_keys WHERE drop_id = :drop_id)
       THEN '1' ELSE '0' END;
