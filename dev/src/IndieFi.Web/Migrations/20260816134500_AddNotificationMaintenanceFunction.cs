using IndieFi.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndieFi.Web.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260816134500_AddNotificationMaintenanceFunction")]
public partial class AddNotificationMaintenanceFunction : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE OR REPLACE FUNCTION public.cleanup_notification_records(
                p_now timestamp with time zone,
                p_batch_size integer)
            RETURNS TABLE (
                content_removed_count integer,
                delivery_deleted_count integer,
                outbox_state_deleted_count integer,
                inbox_state_deleted_count integer)
            LANGUAGE plpgsql
            SECURITY DEFINER
            SET search_path = pg_catalog, public
            AS $function$
            DECLARE
                v_content_removed_count integer;
                v_delivery_deleted_count integer;
                v_outbox_state_deleted_count integer;
                v_inbox_state_deleted_count integer;
            BEGIN
                IF p_now IS NULL OR p_batch_size < 1 OR p_batch_size > 5000 THEN
                    RAISE EXCEPTION 'Invalid notification cleanup arguments.';
                END IF;

                WITH targets AS (
                    SELECT notification.id
                      FROM public.notifications AS notification
                     WHERE notification.expires_at <= p_now
                       AND notification.content_removed_at IS NULL
                     ORDER BY notification.id
                     FOR UPDATE SKIP LOCKED
                     LIMIT p_batch_size)
                UPDATE public.notifications AS notification
                   SET title = NULL,
                       body = NULL,
                       destination_path = NULL,
                       is_web_visible = FALSE,
                       content_removed_at = p_now
                  FROM targets
                 WHERE notification.id = targets.id;
                GET DIAGNOSTICS v_content_removed_count = ROW_COUNT;

                WITH targets AS (
                    SELECT delivery.id
                      FROM public.notification_deliveries AS delivery
                      JOIN public.notifications AS notification
                        ON notification.id = delivery.notification_id
                     WHERE notification.expires_at <= p_now - INTERVAL '30 days'
                       AND delivery.status IN ('Suppressed', 'Delivered', 'Expired')
                     ORDER BY delivery.id
                     FOR UPDATE OF delivery SKIP LOCKED
                     LIMIT p_batch_size)
                DELETE FROM public.notification_deliveries AS delivery
                 USING targets
                 WHERE delivery.id = targets.id;
                GET DIAGNOSTICS v_delivery_deleted_count = ROW_COUNT;

                WITH targets AS (
                    SELECT state.outbox_id
                      FROM public.message_outbox_states AS state
                     WHERE state.delivered <= p_now - INTERVAL '7 days'
                       AND NOT EXISTS (
                           SELECT 1
                             FROM public.message_outbox_messages AS message
                            WHERE message.outbox_id = state.outbox_id)
                     ORDER BY state.delivered, state.outbox_id
                     FOR UPDATE SKIP LOCKED
                     LIMIT p_batch_size)
                DELETE FROM public.message_outbox_states AS state
                 USING targets
                 WHERE state.outbox_id = targets.outbox_id;
                GET DIAGNOSTICS v_outbox_state_deleted_count = ROW_COUNT;

                WITH targets AS (
                    SELECT state.id
                      FROM public.message_inbox_states AS state
                     WHERE state.consumed IS NOT NULL
                       AND state.expiration_time <= p_now
                       AND NOT EXISTS (
                           SELECT 1
                             FROM public.message_outbox_messages AS message
                            WHERE message.inbox_message_id = state.message_id
                              AND message.inbox_consumer_id = state.consumer_id)
                     ORDER BY state.expiration_time, state.id
                     FOR UPDATE SKIP LOCKED
                     LIMIT p_batch_size)
                DELETE FROM public.message_inbox_states AS state
                 USING targets
                 WHERE state.id = targets.id;
                GET DIAGNOSTICS v_inbox_state_deleted_count = ROW_COUNT;

                RETURN QUERY SELECT
                    v_content_removed_count,
                    v_delivery_deleted_count,
                    v_outbox_state_deleted_count,
                    v_inbox_state_deleted_count;
            END;
            $function$;

            REVOKE ALL ON FUNCTION public.cleanup_notification_records(
                timestamp with time zone,
                integer)
                FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION public.cleanup_notification_records(
                timestamp with time zone,
                integer)
                TO indiefi_app;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP FUNCTION IF EXISTS public.cleanup_notification_records(
                timestamp with time zone,
                integer);
            """);
    }
}
