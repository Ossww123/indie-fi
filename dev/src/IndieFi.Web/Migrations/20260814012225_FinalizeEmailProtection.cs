using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class FinalizeEmailProtection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // This migration deliberately cannot access long-lived encryption keys.
            // The separate account migration tool must complete and verify the data first.
            migrationBuilder.Sql(
                """
                DO $migration$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM users
                        WHERE email_ciphertext IS NULL
                           OR email_nonce IS NULL
                           OR email_authentication_tag IS NULL
                           OR email_encryption_key_id IS NULL
                    ) THEN
                        RAISE EXCEPTION 'Email protection backfill is incomplete.';
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM users AS user_account
                        WHERE NOT EXISTS (
                            SELECT 1
                            FROM user_email_lookups AS lookup
                            WHERE lookup.user_id = user_account.id
                        )
                    ) THEN
                        RAISE EXCEPTION 'At least one email lookup is required for every user.';
                    END IF;
                END
                $migration$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_users_email_protection_complete",
                table: "users");

            migrationBuilder.AlterColumn<byte[]>(
                name: "email_nonce",
                table: "users",
                type: "bytea",
                nullable: false,
                oldClrType: typeof(byte[]),
                oldType: "bytea",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "email_encryption_key_id",
                table: "users",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.AlterColumn<byte[]>(
                name: "email_ciphertext",
                table: "users",
                type: "bytea",
                nullable: false,
                oldClrType: typeof(byte[]),
                oldType: "bytea",
                oldNullable: true);

            migrationBuilder.AlterColumn<byte[]>(
                name: "email_authentication_tag",
                table: "users",
                type: "bytea",
                nullable: false,
                oldClrType: typeof(byte[]),
                oldType: "bytea",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_users_email_protection_complete",
                table: "users",
                sql: "octet_length(email_nonce) = 12 AND octet_length(email_authentication_tag) = 16");

            migrationBuilder.Sql(
                "REVOKE INSERT (email, normalized_email) ON users FROM indiefi_app;");

            migrationBuilder.DropIndex(
                name: "IX_users_normalized_email",
                table: "users");

            migrationBuilder.DropColumn(
                name: "email",
                table: "users");

            migrationBuilder.DropColumn(
                name: "normalized_email",
                table: "users");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException(
                "Plaintext email columns cannot be restored without decrypting account data outside EF migrations.");
        }
    }
}
