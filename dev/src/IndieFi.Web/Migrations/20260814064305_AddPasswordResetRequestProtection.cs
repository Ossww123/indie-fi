using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddPasswordResetRequestProtection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_password_reset_tokens_user_id",
                table: "password_reset_tokens");

            migrationBuilder.DropCheckConstraint(
                name: "CK_authentication_email_outbox_messages_kind",
                table: "authentication_email_outbox_messages");

            migrationBuilder.CreateTable(
                name: "password_reset_request_attempts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: true),
                    request_ip_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    request_ip_hash_key_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_password_reset_request_attempts", x => x.id);
                    table.CheckConstraint("CK_password_reset_request_attempts_ip_hash_length", "octet_length(request_ip_hash) = 32");
                    table.ForeignKey(
                        name: "FK_password_reset_request_attempts_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_password_reset_tokens_user_id_unresolved",
                table: "password_reset_tokens",
                column: "user_id",
                unique: true,
                filter: "used_at IS NULL AND revoked_at IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_authentication_email_outbox_messages_kind",
                table: "authentication_email_outbox_messages",
                sql: "kind IN ('EmailConfirmation', 'PasswordReset')");

            migrationBuilder.CreateIndex(
                name: "IX_password_reset_request_attempts_created_at",
                table: "password_reset_request_attempts",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_password_reset_request_attempts_ip_created_at",
                table: "password_reset_request_attempts",
                columns: new[] { "request_ip_hash_key_id", "request_ip_hash", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_password_reset_request_attempts_user_id_created_at",
                table: "password_reset_request_attempts",
                columns: new[] { "user_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "password_reset_request_attempts");

            migrationBuilder.DropIndex(
                name: "IX_password_reset_tokens_user_id_unresolved",
                table: "password_reset_tokens");

            migrationBuilder.DropCheckConstraint(
                name: "CK_authentication_email_outbox_messages_kind",
                table: "authentication_email_outbox_messages");

            migrationBuilder.CreateIndex(
                name: "IX_password_reset_tokens_user_id",
                table: "password_reset_tokens",
                column: "user_id");

            migrationBuilder.AddCheckConstraint(
                name: "CK_authentication_email_outbox_messages_kind",
                table: "authentication_email_outbox_messages",
                sql: "kind IN ('EmailConfirmation')");
        }
    }
}
