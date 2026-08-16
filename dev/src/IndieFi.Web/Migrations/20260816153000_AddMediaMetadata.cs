using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_games_id_owner_id",
                table: "games",
                columns: new[] { "id", "owner_id" });

            migrationBuilder.CreateTable(
                name: "media_assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<long>(type: "bigint", nullable: false),
                    game_id = table.Column<long>(type: "bigint", nullable: true),
                    profile_user_id = table.Column<long>(type: "bigint", nullable: true),
                    attachment_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    declared_content_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    declared_bytes = table.Column<long>(type: "bigint", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    source_content_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    source_bytes = table.Column<long>(type: "bigint", nullable: true),
                    source_etag = table.Column<string>(type: "character varying(130)", maxLength: 130, nullable: true),
                    detected_content_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    pixel_width = table.Column<int>(type: "integer", nullable: true),
                    pixel_height = table.Column<int>(type: "integer", nullable: true),
                    frame_count = table.Column<int>(type: "integer", nullable: true),
                    processing_attempt_count = table.Column<int>(type: "integer", nullable: false),
                    processing_lease_id = table.Column<Guid>(type: "uuid", nullable: true),
                    processing_lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_processing_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    is_current = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    sort_order = table.Column<int>(type: "integer", nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    status_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    upload_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now() + interval '24 hours'"),
                    source_deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deletion_completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tombstone_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_assets", x => x.id);
                    table.CheckConstraint("CK_media_assets_declared_upload", "declared_content_type IN ('image/jpeg', 'image/png', 'image/webp') AND declared_bytes > 0 AND ((attachment_kind IN ('GameCover', 'GameScreenshot') AND declared_bytes <= 10485760) OR (attachment_kind = 'ProfileImage' AND declared_bytes <= 5242880))");
                    table.CheckConstraint("CK_media_assets_detected_image", "(detected_content_type IS NULL AND pixel_width IS NULL AND pixel_height IS NULL AND frame_count IS NULL) OR (detected_content_type IS NOT NULL AND pixel_width IS NOT NULL AND pixel_height IS NOT NULL AND frame_count IS NOT NULL AND detected_content_type IN ('image/jpeg', 'image/png', 'image/webp') AND frame_count = 1 AND pixel_width > 0 AND pixel_height > 0 AND ((attachment_kind IN ('GameCover', 'GameScreenshot') AND pixel_width <= 12000 AND pixel_height <= 12000 AND pixel_width::bigint * pixel_height <= 40000000) OR (attachment_kind = 'ProfileImage' AND pixel_width <= 8192 AND pixel_height <= 8192 AND pixel_width::bigint * pixel_height <= 16000000)))");
                    table.CheckConstraint("CK_media_assets_failure", "(status = 'Failed' AND failure_code IS NOT NULL AND failure_code ~ '^[a-z0-9][a-z0-9_.-]{0,63}$') OR (status <> 'Failed' AND failure_code IS NULL)");
                    table.CheckConstraint("CK_media_assets_id", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_media_assets_processing", "processing_attempt_count BETWEEN 0 AND 6 AND ((processing_lease_id IS NULL AND processing_lease_expires_at IS NULL) OR (status = 'Processing' AND processing_lease_id <> '00000000-0000-0000-0000-000000000000'::uuid AND processing_lease_expires_at IS NOT NULL)) AND (status <> 'Processing' OR (processing_attempt_count > 0 AND processing_lease_id IS NOT NULL)) AND (next_processing_attempt_at IS NULL OR status = 'ProcessingPending') AND (status <> 'UploadPending' OR processing_attempt_count = 0)");
                    table.CheckConstraint("CK_media_assets_publication", "is_current = (status = 'Ready') AND ((is_current = FALSE AND sort_order IS NULL AND published_at IS NULL) OR (is_current = TRUE AND published_at IS NOT NULL AND ((attachment_kind = 'GameScreenshot' AND sort_order BETWEEN 0 AND 7) OR (attachment_kind IN ('GameCover', 'ProfileImage') AND sort_order IS NULL)))) AND (status <> 'Ready' OR detected_content_type IS NOT NULL)");
                    table.CheckConstraint("CK_media_assets_revision", "revision > 0");
                    table.CheckConstraint("CK_media_assets_source_metadata", "(source_content_type IS NULL AND source_bytes IS NULL AND source_etag IS NULL) OR (source_content_type IS NOT NULL AND source_bytes IS NOT NULL AND source_etag IS NOT NULL AND source_content_type = declared_content_type AND source_bytes = declared_bytes AND char_length(source_etag) BETWEEN 3 AND 130 AND left(source_etag, 1) = chr(34) AND right(source_etag, 1) = chr(34))");
                    table.CheckConstraint("CK_media_assets_source_state", "(status IN ('UploadPending', 'Expired') AND source_content_type IS NULL) OR (status IN ('ProcessingPending', 'Processing', 'Ready') AND source_content_type IS NOT NULL) OR status IN ('Failed', 'DeletePending', 'Deleted')");
                    table.CheckConstraint("CK_media_assets_status", "status IN ('UploadPending', 'ProcessingPending', 'Processing', 'Ready', 'Failed', 'Expired', 'DeletePending', 'Deleted')");
                    table.CheckConstraint("CK_media_assets_target_shape", "(attachment_kind IN ('GameCover', 'GameScreenshot') AND game_id IS NOT NULL AND profile_user_id IS NULL) OR (attachment_kind = 'ProfileImage' AND game_id IS NULL AND profile_user_id IS NOT NULL AND profile_user_id = owner_user_id)");
                    table.CheckConstraint("CK_media_assets_timestamps", "updated_at >= created_at AND status_changed_at >= created_at AND status_changed_at <= updated_at AND upload_expires_at = created_at + interval '24 hours' AND (published_at IS NULL OR published_at >= created_at) AND (source_deleted_at IS NULL OR source_deleted_at >= created_at) AND (deletion_completed_at IS NULL OR deletion_completed_at >= created_at)");
                    table.CheckConstraint("CK_media_assets_tombstone", "(deletion_completed_at IS NULL AND tombstone_expires_at IS NULL) OR (status IN ('Failed', 'Expired', 'Deleted') AND deletion_completed_at IS NOT NULL AND tombstone_expires_at >= deletion_completed_at + interval '30 days')");
                    table.ForeignKey(
                        name: "FK_media_assets_games_game_id_owner_user_id",
                        columns: x => new { x.game_id, x.owner_user_id },
                        principalTable: "games",
                        principalColumns: new[] { "id", "owner_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_media_assets_user_profiles_profile_user_id",
                        column: x => x.profile_user_id,
                        principalTable: "user_profiles",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_media_assets_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "media_upload_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    media_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    url_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now() + interval '15 minutes'"),
                    superseded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_upload_attempts", x => x.id);
                    table.CheckConstraint("CK_media_upload_attempts_id", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_media_upload_attempts_lifetime", "url_expires_at > issued_at AND url_expires_at <= issued_at + interval '15 minutes'");
                    table.CheckConstraint("CK_media_upload_attempts_outcome", "(superseded_at IS NULL OR superseded_at >= issued_at) AND (completed_at IS NULL OR completed_at >= issued_at) AND NOT (superseded_at IS NOT NULL AND completed_at IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_media_upload_attempts_media_assets_media_id",
                        column: x => x.media_id,
                        principalTable: "media_assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_media_assets_active_game_cover_candidate",
                table: "media_assets",
                columns: new[] { "game_id", "attachment_kind" },
                unique: true,
                filter: "attachment_kind = 'GameCover' AND status IN ('UploadPending', 'ProcessingPending', 'Processing')");

            migrationBuilder.CreateIndex(
                name: "IX_media_assets_active_profile_image_candidate",
                table: "media_assets",
                columns: new[] { "profile_user_id", "attachment_kind" },
                unique: true,
                filter: "attachment_kind = 'ProfileImage' AND status IN ('UploadPending', 'ProcessingPending', 'Processing')");

            migrationBuilder.CreateIndex(
                name: "IX_media_assets_current_game_cover",
                table: "media_assets",
                columns: new[] { "game_id", "attachment_kind", "is_current" },
                unique: true,
                filter: "is_current = TRUE AND attachment_kind = 'GameCover'");

            migrationBuilder.CreateIndex(
                name: "IX_media_assets_current_game_screenshot_order",
                table: "media_assets",
                columns: new[] { "game_id", "sort_order" },
                unique: true,
                filter: "is_current = TRUE AND attachment_kind = 'GameScreenshot'");

            migrationBuilder.CreateIndex(
                name: "IX_media_assets_current_profile_image",
                table: "media_assets",
                columns: new[] { "profile_user_id", "is_current" },
                unique: true,
                filter: "is_current = TRUE AND attachment_kind = 'ProfileImage'");

            migrationBuilder.CreateIndex(
                name: "IX_media_assets_game_id_attachment_kind_status",
                table: "media_assets",
                columns: new[] { "game_id", "attachment_kind", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_media_assets_game_id_owner_user_id",
                table: "media_assets",
                columns: new[] { "game_id", "owner_user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_media_assets_owner_user_id_status",
                table: "media_assets",
                columns: new[] { "owner_user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_media_assets_profile_user_id_status",
                table: "media_assets",
                columns: new[] { "profile_user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_media_assets_status_next_processing_attempt_at",
                table: "media_assets",
                columns: new[] { "status", "next_processing_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "IX_media_assets_status_tombstone_expires_at",
                table: "media_assets",
                columns: new[] { "status", "tombstone_expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_media_assets_status_upload_expires_at",
                table: "media_assets",
                columns: new[] { "status", "upload_expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_media_upload_attempts_current",
                table: "media_upload_attempts",
                column: "media_id",
                unique: true,
                filter: "superseded_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_media_upload_attempts_pending_expiry",
                table: "media_upload_attempts",
                column: "url_expires_at",
                filter: "superseded_at IS NULL AND completed_at IS NULL");

            migrationBuilder.Sql(
                """
                -- Target identity, declared upload facts, and revision are immutable
                -- after reservation. Runtime code can advance only lifecycle and
                -- verification fields, while tombstone cleanup may remove the row.
                REVOKE ALL ON media_assets FROM indiefi_app;
                GRANT SELECT, INSERT, DELETE ON media_assets TO indiefi_app;
                GRANT UPDATE (
                    status,
                    source_content_type,
                    source_bytes,
                    source_etag,
                    detected_content_type,
                    pixel_width,
                    pixel_height,
                    frame_count,
                    processing_attempt_count,
                    processing_lease_id,
                    processing_lease_expires_at,
                    next_processing_attempt_at,
                    failure_code,
                    is_current,
                    sort_order,
                    published_at,
                    updated_at,
                    status_changed_at,
                    source_deleted_at,
                    deletion_completed_at,
                    tombstone_expires_at)
                    ON media_assets TO indiefi_app;

                -- A signed URL's identity and lifetime never change. Reissue and
                -- completion only close the corresponding attempt.
                REVOKE ALL ON media_upload_attempts FROM indiefi_app;
                GRANT SELECT, INSERT ON media_upload_attempts TO indiefi_app;
                GRANT UPDATE (superseded_at, completed_at)
                    ON media_upload_attempts TO indiefi_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "media_upload_attempts");

            migrationBuilder.DropTable(
                name: "media_assets");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_games_id_owner_id",
                table: "games");
        }
    }
}
