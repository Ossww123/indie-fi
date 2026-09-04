using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class ExpandAccountProtection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "password_hash",
                table: "users",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<string>(
                name: "account_status",
                table: "users",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Active");

            migrationBuilder.AddColumn<byte[]>(
                name: "email_authentication_tag",
                table: "users",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "email_ciphertext",
                table: "users",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "email_confirmed_at",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "email_encryption_key_id",
                table: "users",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "email_nonce",
                table: "users",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "session_version",
                table: "users",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "suspended_until",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            // Existing accounts predate email confirmation, so preserve their login
            // access. New accounts remain unconfirmed until the confirmation flow exists.
            migrationBuilder.Sql(
                "UPDATE users SET email_confirmed_at = created_at;");

            // users has deliberately restricted column-level INSERT permission.
            migrationBuilder.Sql(
                """
                GRANT INSERT (
                    account_status,
                    email_ciphertext,
                    email_nonce,
                    email_authentication_tag,
                    email_encryption_key_id,
                    email_confirmed_at,
                    session_version,
                    suspended_until)
                ON users TO indiefi_app;
                """);

            migrationBuilder.CreateTable(
                name: "email_confirmation_tokens",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_confirmation_tokens", x => x.id);
                    table.CheckConstraint("CK_email_confirmation_tokens_hash_length", "octet_length(token_hash) = 32");
                    table.ForeignKey(
                        name: "FK_email_confirmation_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "external_logins",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider_user_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    linked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_logins", x => x.id);
                    table.ForeignKey(
                        name: "FK_external_logins_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "password_reset_tokens",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_password_reset_tokens", x => x.id);
                    table.CheckConstraint("CK_password_reset_tokens_hash_length", "octet_length(token_hash) = 32");
                    table.ForeignKey(
                        name: "FK_password_reset_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_email_lookups",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    key_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    value = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_email_lookups", x => new { x.user_id, x.key_id });
                    table.CheckConstraint("CK_user_email_lookups_value_length", "octet_length(value) = 32");
                    table.ForeignKey(
                        name: "FK_user_email_lookups_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_users_email_protection_complete",
                table: "users",
                sql: "(email_ciphertext IS NULL AND email_nonce IS NULL AND email_authentication_tag IS NULL AND email_encryption_key_id IS NULL) OR (email_ciphertext IS NOT NULL AND octet_length(email_nonce) = 12 AND octet_length(email_authentication_tag) = 16 AND email_encryption_key_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_users_session_version_positive",
                table: "users",
                sql: "session_version >= 1");

            migrationBuilder.AddCheckConstraint(
                name: "CK_users_suspension_shape",
                table: "users",
                sql: "(account_status = 'TemporarilySuspended' AND suspended_until IS NOT NULL) OR (account_status IN ('Active', 'IndefinitelySuspended') AND suspended_until IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_email_confirmation_tokens_token_hash",
                table: "email_confirmation_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_email_confirmation_tokens_user_id",
                table: "email_confirmation_tokens",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_external_logins_provider_provider_user_id",
                table: "external_logins",
                columns: new[] { "provider", "provider_user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_external_logins_user_id_provider",
                table: "external_logins",
                columns: new[] { "user_id", "provider" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_password_reset_tokens_token_hash",
                table: "password_reset_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_password_reset_tokens_user_id",
                table: "password_reset_tokens",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_user_email_lookups_key_id_value",
                table: "user_email_lookups",
                columns: new[] { "key_id", "value" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                REVOKE INSERT (
                    account_status,
                    email_ciphertext,
                    email_nonce,
                    email_authentication_tag,
                    email_encryption_key_id,
                    email_confirmed_at,
                    session_version,
                    suspended_until)
                ON users FROM indiefi_app;
                """);

            migrationBuilder.DropTable(
                name: "email_confirmation_tokens");

            migrationBuilder.DropTable(
                name: "external_logins");

            migrationBuilder.DropTable(
                name: "password_reset_tokens");

            migrationBuilder.DropTable(
                name: "user_email_lookups");

            migrationBuilder.DropCheckConstraint(
                name: "CK_users_email_protection_complete",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "CK_users_session_version_positive",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "CK_users_suspension_shape",
                table: "users");

            migrationBuilder.DropColumn(
                name: "account_status",
                table: "users");

            migrationBuilder.DropColumn(
                name: "email_authentication_tag",
                table: "users");

            migrationBuilder.DropColumn(
                name: "email_ciphertext",
                table: "users");

            migrationBuilder.DropColumn(
                name: "email_confirmed_at",
                table: "users");

            migrationBuilder.DropColumn(
                name: "email_encryption_key_id",
                table: "users");

            migrationBuilder.DropColumn(
                name: "email_nonce",
                table: "users");

            migrationBuilder.DropColumn(
                name: "session_version",
                table: "users");

            migrationBuilder.DropColumn(
                name: "suspended_until",
                table: "users");

            migrationBuilder.AlterColumn<string>(
                name: "password_hash",
                table: "users",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
