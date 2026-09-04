\set ON_ERROR_STOP on
SET statement_timeout = '30s';
SET default_transaction_read_only = on;

SELECT 'authentication_email.total', count(*)::text
  FROM authentication_email_outbox_messages;

SELECT 'authentication_email.due', count(*)::text
  FROM authentication_email_outbox_messages
 WHERE next_attempt_at <= now();

SELECT 'authentication_email.oldest_due_seconds',
       coalesce(greatest(0, extract(epoch FROM now() - min(next_attempt_at)))::bigint, 0)::text
  FROM authentication_email_outbox_messages
 WHERE next_attempt_at <= now();

SELECT 'notification_email.' || lower(expected.status), count(delivery.id)::text
  FROM (VALUES ('Suppressed'), ('Pending'), ('Delivered'), ('Failed'), ('Expired'))
       AS expected(status)
  LEFT JOIN notification_deliveries AS delivery
    ON delivery.channel = 'Email'
   AND delivery.status = expected.status
 GROUP BY expected.status
 ORDER BY 1;

SELECT 'notification_email.due', count(*)::text
  FROM notification_deliveries
 WHERE channel = 'Email'
   AND status = 'Pending'
   AND next_attempt_at <= now();

SELECT 'notification_email.oldest_due_seconds',
       coalesce(greatest(0, extract(epoch FROM now() - min(next_attempt_at)))::bigint, 0)::text
  FROM notification_deliveries
 WHERE channel = 'Email'
   AND status = 'Pending'
   AND next_attempt_at <= now();

SELECT 'media.' || lower(expected.status), count(media.id)::text
  FROM (VALUES
        ('UploadPending'), ('ProcessingPending'), ('Processing'), ('Ready'),
        ('Failed'), ('Expired'), ('DeletePending'), ('Deleted')) AS expected(status)
  LEFT JOIN media_assets AS media ON media.status = expected.status
 GROUP BY expected.status
 ORDER BY 1;

SELECT 'media.processing_due', count(*)::text
  FROM media_assets
 WHERE status = 'ProcessingPending'
   AND coalesce(next_processing_attempt_at, now()) <= now();

SELECT 'media.expired_leases', count(*)::text
  FROM media_assets
 WHERE status = 'Processing'
   AND processing_lease_expires_at <= now();

SELECT 'messaging.outbox_pending', count(*)::text
  FROM message_outbox_messages;
