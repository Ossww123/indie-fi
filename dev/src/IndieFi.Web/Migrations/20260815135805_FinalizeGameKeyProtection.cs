using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class FinalizeGameKeyProtection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // This migration deliberately cannot access long-lived protection keys.
            // The 6-4-2 rotation tool must complete and verify the data first.
            migrationBuilder.Sql(
                """
                DO $migration$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM game_keys
                        WHERE ciphertext IS NULL
                           OR nonce IS NULL
                           OR authentication_tag IS NULL
                           OR encryption_key_id IS NULL
                    ) THEN
                        RAISE EXCEPTION 'Game key protection backfill is incomplete.';
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM game_keys AS game_key
                        WHERE NOT EXISTS (
                            SELECT 1
                            FROM game_key_fingerprints AS fingerprint
                            WHERE fingerprint.game_key_id = game_key.id
                        )
                    ) THEN
                        RAISE EXCEPTION 'At least one fingerprint is required for every game key.';
                    END IF;
                END
                $migration$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_game_keys_encryption_shape",
                table: "game_keys");

            migrationBuilder.DropCheckConstraint(
                name: "CK_game_keys_storage_present",
                table: "game_keys");

            migrationBuilder.DropColumn(
                name: "key_value",
                table: "game_keys");

            migrationBuilder.AlterColumn<byte[]>(
                name: "nonce",
                table: "game_keys",
                type: "bytea",
                nullable: false,
                oldClrType: typeof(byte[]),
                oldType: "bytea",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "encryption_key_id",
                table: "game_keys",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.AlterColumn<byte[]>(
                name: "ciphertext",
                table: "game_keys",
                type: "bytea",
                nullable: false,
                oldClrType: typeof(byte[]),
                oldType: "bytea",
                oldNullable: true);

            migrationBuilder.AlterColumn<byte[]>(
                name: "authentication_tag",
                table: "game_keys",
                type: "bytea",
                nullable: false,
                oldClrType: typeof(byte[]),
                oldType: "bytea",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_game_keys_encryption_shape",
                table: "game_keys",
                sql: "octet_length(ciphertext) BETWEEN 1 AND 800 AND octet_length(nonce) = 12 AND octet_length(authentication_tag) = 16 AND char_length(encryption_key_id) BETWEEN 1 AND 100 AND encryption_key_id = btrim(encryption_key_id)");

            migrationBuilder.Sql(
                """
                REVOKE ALL ON game_keys FROM indiefi_app;
                GRANT SELECT ON game_keys TO indiefi_app;
                -- PostgreSQL requires UPDATE on at least one column for SELECT FOR UPDATE.
                GRANT UPDATE (created_at) ON game_keys TO indiefi_app;
                GRANT INSERT (
                    drop_id,
                    ciphertext,
                    nonce,
                    authentication_tag,
                    encryption_key_id)
                    ON game_keys TO indiefi_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException(
                "Plaintext game key storage cannot be restored without decrypting "
                + "game key data outside EF migrations.");
        }
    }
}
