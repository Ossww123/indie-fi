using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class ExpandEncryptedGameKeyInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "key_value",
                table: "game_keys",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<byte[]>(
                name: "authentication_tag",
                table: "game_keys",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "ciphertext",
                table: "game_keys",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "encryption_key_id",
                table: "game_keys",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "nonce",
                table: "game_keys",
                type: "bytea",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "game_key_fingerprints",
                columns: table => new
                {
                    game_key_id = table.Column<long>(type: "bigint", nullable: false),
                    key_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    value = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_game_key_fingerprints", x => new { x.game_key_id, x.key_id });
                    table.CheckConstraint("CK_game_key_fingerprints_key_id_shape", "char_length(key_id) BETWEEN 1 AND 100 AND key_id = btrim(key_id)");
                    table.CheckConstraint("CK_game_key_fingerprints_value_length", "octet_length(value) = 32");
                    table.ForeignKey(
                        name: "FK_game_key_fingerprints_game_keys_game_key_id",
                        column: x => x.game_key_id,
                        principalTable: "game_keys",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_game_keys_encryption_shape",
                table: "game_keys",
                sql: "num_nonnulls(ciphertext, nonce, authentication_tag, encryption_key_id) = 0 OR (num_nonnulls(ciphertext, nonce, authentication_tag, encryption_key_id) = 4 AND octet_length(ciphertext) BETWEEN 1 AND 800 AND octet_length(nonce) = 12 AND octet_length(authentication_tag) = 16 AND char_length(encryption_key_id) BETWEEN 1 AND 100 AND encryption_key_id = btrim(encryption_key_id))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_game_keys_storage_present",
                table: "game_keys",
                sql: "key_value IS NOT NULL OR ciphertext IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_game_key_fingerprints_key_id_value",
                table: "game_key_fingerprints",
                columns: new[] { "key_id", "value" },
                unique: true);

            migrationBuilder.Sql(
                """
                REVOKE ALL ON game_keys FROM indiefi_app;
                GRANT SELECT ON game_keys TO indiefi_app;
                -- PostgreSQL requires UPDATE on at least one column for SELECT FOR UPDATE.
                -- The claim path only locks rows; key material and ownership stay read-only.
                GRANT UPDATE (created_at) ON game_keys TO indiefi_app;
                GRANT INSERT (
                    drop_id,
                    key_value,
                    ciphertext,
                    nonce,
                    authentication_tag,
                    encryption_key_id)
                    ON game_keys TO indiefi_app;

                REVOKE ALL ON game_key_fingerprints FROM indiefi_app;
                GRANT SELECT ON game_key_fingerprints TO indiefi_app;
                GRANT INSERT (game_key_id, key_id, value)
                    ON game_key_fingerprints TO indiefi_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $migration$
                BEGIN
                    IF EXISTS (SELECT 1 FROM game_keys WHERE key_value IS NULL) THEN
                        RAISE EXCEPTION 'Encrypted-only game keys cannot be downgraded.';
                    END IF;
                END
                $migration$;
                """);

            migrationBuilder.DropTable(
                name: "game_key_fingerprints");

            migrationBuilder.DropCheckConstraint(
                name: "CK_game_keys_encryption_shape",
                table: "game_keys");

            migrationBuilder.DropCheckConstraint(
                name: "CK_game_keys_storage_present",
                table: "game_keys");

            migrationBuilder.DropColumn(
                name: "authentication_tag",
                table: "game_keys");

            migrationBuilder.DropColumn(
                name: "ciphertext",
                table: "game_keys");

            migrationBuilder.DropColumn(
                name: "encryption_key_id",
                table: "game_keys");

            migrationBuilder.DropColumn(
                name: "nonce",
                table: "game_keys");

            migrationBuilder.AlterColumn<string>(
                name: "key_value",
                table: "game_keys",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.Sql(
                "GRANT SELECT, INSERT, UPDATE, DELETE ON game_keys TO indiefi_app;");
        }
    }
}
