\set ON_ERROR_STOP on
SET statement_timeout = '30s';
SET default_transaction_read_only = on;

WITH run_users AS (
    SELECT user_id
      FROM user_profiles
     WHERE biography = 'operational-validation:' || :'run_id'
),
run_media AS (
    SELECT media.*
      FROM media_assets AS media
     WHERE media.owner_user_id IN (SELECT user_id FROM run_users)
),
run_notifications AS (
    SELECT notification.*
      FROM notifications AS notification
     WHERE notification.user_id IN (SELECT user_id FROM run_users)
       AND notification.kind = 'KeyDropOpened'
       AND notification.source_id = :drop_id
)
SELECT 'accounts.total', count(*)::text FROM run_users
UNION ALL SELECT 'preferences.total', count(*)::text
  FROM notification_preferences
 WHERE user_id IN (SELECT user_id FROM run_users)
UNION ALL SELECT 'preferences.keydrop_email_enabled', count(*)::text
  FROM notification_preferences
 WHERE user_id IN (SELECT user_id FROM run_users)
   AND kind = 'KeyDropOpened'
   AND email_enabled
UNION ALL SELECT 'subscriptions.total', count(*)::text
  FROM key_drop_subscriptions
 WHERE drop_id = :drop_id
   AND user_id IN (SELECT user_id FROM run_users)
UNION ALL SELECT 'notifications.total', count(*)::text FROM run_notifications
UNION ALL SELECT 'notifications.distinct_events', count(DISTINCT event_id)::text
  FROM run_notifications
UNION ALL SELECT 'notifications.duplicate_event_groups', count(*)::text
  FROM (
        SELECT event_id
          FROM run_notifications
         GROUP BY event_id
        HAVING count(*) > 1
       ) AS duplicates
UNION ALL SELECT 'deliveries.web', count(*)::text
  FROM notification_deliveries AS delivery
  JOIN run_notifications AS notification ON notification.id = delivery.notification_id
 WHERE delivery.channel = 'Web'
UNION ALL SELECT 'deliveries.email', count(*)::text
  FROM notification_deliveries AS delivery
  JOIN run_notifications AS notification ON notification.id = delivery.notification_id
 WHERE delivery.channel = 'Email'
UNION ALL SELECT 'deliveries.email_suppressed', count(*)::text
  FROM notification_deliveries AS delivery
  JOIN run_notifications AS notification ON notification.id = delivery.notification_id
 WHERE delivery.channel = 'Email'
   AND delivery.status = 'Suppressed'
UNION ALL SELECT 'deliveries.email_delivered', count(*)::text
  FROM notification_deliveries AS delivery
  JOIN run_notifications AS notification ON notification.id = delivery.notification_id
 WHERE delivery.channel = 'Email'
   AND delivery.status = 'Delivered'
UNION ALL SELECT 'deliveries.unresolved', count(*)::text
  FROM notification_deliveries AS delivery
  JOIN run_notifications AS notification ON notification.id = delivery.notification_id
 WHERE delivery.status IN ('Pending', 'Failed')
UNION ALL SELECT 'media.total', count(*)::text FROM run_media
UNION ALL SELECT 'media.ready', count(*)::text FROM run_media WHERE status = 'Ready'
UNION ALL SELECT 'media.failed', count(*)::text FROM run_media WHERE status = 'Failed'
UNION ALL SELECT 'media.in_flight', count(*)::text
  FROM run_media
 WHERE status IN ('UploadPending', 'ProcessingPending', 'Processing', 'DeletePending')
UNION ALL SELECT 'media.current', count(*)::text FROM run_media WHERE is_current
UNION ALL SELECT 'media.active_leases', count(*)::text
  FROM run_media
 WHERE processing_lease_id IS NOT NULL
    OR processing_lease_expires_at IS NOT NULL
UNION ALL SELECT 'media.processing_attempts', coalesce(sum(processing_attempt_count), 0)::text
  FROM run_media
UNION ALL SELECT 'message_outbox.pending', count(*)::text FROM message_outbox_messages;
