using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "key_drop_subscriptions",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    drop_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_key_drop_subscriptions", x => new { x.user_id, x.drop_id });
                    table.ForeignKey(
                        name: "FK_key_drop_subscriptions_key_drops_drop_id",
                        column: x => x.drop_id,
                        principalTable: "key_drops",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_key_drop_subscriptions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notification_preferences",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    web_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    email_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_preferences", x => new { x.user_id, x.kind });
                    table.CheckConstraint("CK_notification_preferences_kind", "kind IN ('ReviewAssigned', 'ReviewDeadlineSoon', 'ReviewExpired', 'FeedbackCompleted', 'KeyDropOpened', 'FavoriteGameKeyDropCreated', 'FollowedUserGamePublished', 'ReportResolved', 'AccountStatusChanged')");
                    table.CheckConstraint("CK_notification_preferences_mandatory", "kind <> 'AccountStatusChanged' OR (web_enabled = TRUE AND email_enabled = TRUE)");
                    table.ForeignKey(
                        name: "FK_notification_preferences_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    source_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    source_id = table.Column<long>(type: "bigint", nullable: false),
                    title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    body = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    destination_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    is_web_visible = table.Column<bool>(type: "boolean", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    content_removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notifications", x => x.id);
                    table.CheckConstraint("CK_notifications_content_shape", "(content_removed_at IS NULL AND title IS NOT NULL AND body IS NOT NULL AND length(btrim(title)) > 0 AND length(btrim(body)) > 0) OR (content_removed_at IS NOT NULL AND content_removed_at >= created_at AND title IS NULL AND body IS NULL AND destination_path IS NULL AND is_web_visible = FALSE)");
                    table.CheckConstraint("CK_notifications_destination_path", "destination_path IS NULL OR (destination_path LIKE '/%' AND destination_path NOT LIKE '//%')");
                    table.CheckConstraint("CK_notifications_event_id", "event_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_notifications_kind", "kind IN ('ReviewAssigned', 'ReviewDeadlineSoon', 'ReviewExpired', 'FeedbackCompleted', 'KeyDropOpened', 'FavoriteGameKeyDropCreated', 'FollowedUserGamePublished', 'ReportResolved', 'AccountStatusChanged')");
                    table.CheckConstraint("CK_notifications_lifetime", "occurred_at <= created_at AND expires_at > created_at");
                    table.CheckConstraint("CK_notifications_read_shape", "read_at IS NULL OR (is_web_visible = TRUE AND read_at >= created_at)");
                    table.CheckConstraint("CK_notifications_source", "source_id > 0 AND length(btrim(source_type)) > 0");
                    table.ForeignKey(
                        name: "FK_notifications_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notification_deliveries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    notification_id = table.Column<long>(type: "bigint", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_deliveries", x => x.id);
                    table.CheckConstraint("CK_notification_deliveries_attempt_count", "attempt_count >= 0");
                    table.CheckConstraint("CK_notification_deliveries_channel", "channel IN ('Web', 'Email')");
                    table.CheckConstraint("CK_notification_deliveries_message_id", "message_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_notification_deliveries_state_shape", "(status = 'Suppressed' AND attempt_count = 0 AND next_attempt_at IS NULL AND delivered_at IS NULL AND failed_at IS NULL) OR (status = 'Pending' AND next_attempt_at IS NOT NULL AND delivered_at IS NULL AND failed_at IS NULL) OR (status = 'Delivered' AND next_attempt_at IS NULL AND delivered_at IS NOT NULL AND failed_at IS NULL) OR (status IN ('Failed', 'Expired') AND next_attempt_at IS NULL AND delivered_at IS NULL AND failed_at IS NOT NULL)");
                    table.CheckConstraint("CK_notification_deliveries_status", "status IN ('Suppressed', 'Pending', 'Delivered', 'Failed', 'Expired')");
                    table.CheckConstraint("CK_notification_deliveries_timestamp_order", "(next_attempt_at IS NULL OR next_attempt_at >= created_at) AND (last_attempt_at IS NULL OR last_attempt_at >= created_at) AND (delivered_at IS NULL OR delivered_at >= created_at) AND (failed_at IS NULL OR failed_at >= created_at)");
                    table.ForeignKey(
                        name: "FK_notification_deliveries_notifications_notification_id",
                        column: x => x.notification_id,
                        principalTable: "notifications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_key_drop_subscriptions_drop_id",
                table: "key_drop_subscriptions",
                column: "drop_id");

            migrationBuilder.CreateIndex(
                name: "IX_notification_deliveries_message_id",
                table: "notification_deliveries",
                column: "message_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notification_deliveries_notification_id_channel",
                table: "notification_deliveries",
                columns: new[] { "notification_id", "channel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notification_deliveries_status_next_attempt_at",
                table: "notification_deliveries",
                columns: new[] { "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "IX_notifications_user_created_at",
                table: "notifications",
                columns: new[] { "user_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_notifications_user_id_event_id",
                table: "notifications",
                columns: new[] { "user_id", "event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notifications_user_unread",
                table: "notifications",
                columns: new[] { "user_id", "read_at", "created_at" },
                descending: new[] { false, false, true },
                filter: "is_web_visible = TRUE AND read_at IS NULL AND content_removed_at IS NULL");

            migrationBuilder.Sql(
                """
                REVOKE ALL ON notifications FROM indiefi_app;
                GRANT SELECT, INSERT ON notifications TO indiefi_app;
                GRANT UPDATE (
                    title,
                    body,
                    destination_path,
                    is_web_visible,
                    read_at,
                    content_removed_at)
                    ON notifications TO indiefi_app;
                GRANT USAGE, SELECT
                    ON SEQUENCE notifications_id_seq
                    TO indiefi_app;

                REVOKE ALL ON notification_deliveries FROM indiefi_app;
                GRANT SELECT, INSERT ON notification_deliveries TO indiefi_app;
                GRANT UPDATE (
                    status,
                    attempt_count,
                    next_attempt_at,
                    last_attempt_at,
                    delivered_at,
                    failed_at,
                    last_error_code)
                    ON notification_deliveries TO indiefi_app;
                GRANT USAGE, SELECT
                    ON SEQUENCE notification_deliveries_id_seq
                    TO indiefi_app;

                REVOKE ALL ON notification_preferences FROM indiefi_app;
                GRANT SELECT, INSERT, DELETE ON notification_preferences TO indiefi_app;
                GRANT UPDATE (web_enabled, email_enabled, updated_at)
                    ON notification_preferences TO indiefi_app;

                REVOKE ALL ON key_drop_subscriptions FROM indiefi_app;
                GRANT SELECT, INSERT, DELETE
                    ON key_drop_subscriptions
                    TO indiefi_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "key_drop_subscriptions");

            migrationBuilder.DropTable(
                name: "notification_deliveries");

            migrationBuilder.DropTable(
                name: "notification_preferences");

            migrationBuilder.DropTable(
                name: "notifications");
        }
    }
}
