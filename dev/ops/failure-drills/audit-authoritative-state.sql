\set ON_ERROR_STOP on
SET statement_timeout = '30s';
SET default_transaction_read_only = on;

SELECT 'schema.migration_count', count(*)::text
  FROM "__EFMigrationsHistory";

SELECT 'users.negative_credit_balance', count(*)::text
  FROM users
 WHERE credit_balance < 0;

WITH ledger AS (
    SELECT user_id, coalesce(sum(delta), 0) AS balance
      FROM credit_ledger_entries
     GROUP BY user_id
)
SELECT 'users.credit_ledger_mismatch', count(*)::text
  FROM users AS users
  LEFT JOIN ledger ON ledger.user_id = users.id
 WHERE users.credit_balance <> coalesce(ledger.balance, 0);

SELECT 'game_keys.invalid_encryption_shape', count(*)::text
  FROM game_keys
 WHERE ciphertext IS NULL
    OR nonce IS NULL
    OR authentication_tag IS NULL
    OR encryption_key_id IS NULL
    OR octet_length(ciphertext) NOT BETWEEN 1 AND 800
    OR octet_length(nonce) <> 12
    OR octet_length(authentication_tag) <> 16;

SELECT 'key_grants.total', count(*)::text FROM key_grants;

SELECT 'key_grants.duplicate_drop_user_groups', count(*)::text
  FROM (
        SELECT drop_id, user_id
          FROM key_grants
         GROUP BY drop_id, user_id
        HAVING count(*) > 1
       ) AS duplicates;

SELECT 'key_grants.duplicate_key_groups', count(*)::text
  FROM (
        SELECT key_id
          FROM key_grants
         GROUP BY key_id
        HAVING count(*) > 1
       ) AS duplicates;

SELECT 'reviews.invalid_terminal_shape', count(*)::text
  FROM review_requests
 WHERE (status = 'Completed' AND completed_at IS NULL)
    OR (status = 'Cancelled' AND cancelled_at IS NULL)
    OR (status IN ('Queued', 'Assigned') AND (completed_at IS NOT NULL OR cancelled_at IS NOT NULL));

SELECT 'outbox.authentication_email_pending', count(*)::text
  FROM authentication_email_outbox_messages;

SELECT 'outbox.messaging_pending', count(*)::text
  FROM message_outbox_messages;

SELECT 'notifications.unresolved', count(*)::text
  FROM notification_deliveries
 WHERE status IN ('Pending', 'Failed');

SELECT 'media.in_flight', count(*)::text
  FROM media_assets
 WHERE status IN ('UploadPending', 'ProcessingPending', 'Processing', 'DeletePending');

SELECT 'media.failed', count(*)::text
  FROM media_assets
 WHERE status = 'Failed';
