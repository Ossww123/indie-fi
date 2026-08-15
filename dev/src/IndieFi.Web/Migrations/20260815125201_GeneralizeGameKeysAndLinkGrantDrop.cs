using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class GeneralizeGameKeysAndLinkGrantDrop : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_key_grants_steam_keys_key_id",
                table: "key_grants");

            migrationBuilder.RenameTable(
                name: "steam_keys",
                newName: "game_keys");

            migrationBuilder.RenameIndex(
                name: "IX_steam_keys_drop_id",
                table: "game_keys",
                newName: "IX_game_keys_drop_id");

            migrationBuilder.Sql(
                """
                ALTER TABLE game_keys
                    RENAME CONSTRAINT "PK_steam_keys" TO "PK_game_keys";
                ALTER TABLE game_keys
                    RENAME CONSTRAINT "FK_steam_keys_key_drops_drop_id"
                    TO "FK_game_keys_key_drops_drop_id";
                """);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_game_keys_id_drop_id",
                table: "game_keys",
                columns: new[] { "id", "drop_id" });

            migrationBuilder.CreateIndex(
                name: "IX_key_grants_key_id_drop_id",
                table: "key_grants",
                columns: new[] { "key_id", "drop_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_key_grants_game_keys_key_id_drop_id",
                table: "key_grants",
                columns: new[] { "key_id", "drop_id" },
                principalTable: "game_keys",
                principalColumns: new[] { "id", "drop_id" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_key_grants_game_keys_key_id_drop_id",
                table: "key_grants");

            migrationBuilder.DropIndex(
                name: "IX_key_grants_key_id_drop_id",
                table: "key_grants");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_game_keys_id_drop_id",
                table: "game_keys");

            migrationBuilder.RenameTable(
                name: "game_keys",
                newName: "steam_keys");

            migrationBuilder.RenameIndex(
                name: "IX_game_keys_drop_id",
                table: "steam_keys",
                newName: "IX_steam_keys_drop_id");

            migrationBuilder.Sql(
                """
                ALTER TABLE steam_keys
                    RENAME CONSTRAINT "PK_game_keys" TO "PK_steam_keys";
                ALTER TABLE steam_keys
                    RENAME CONSTRAINT "FK_game_keys_key_drops_drop_id"
                    TO "FK_steam_keys_key_drops_drop_id";
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_key_grants_steam_keys_key_id",
                table: "key_grants",
                column: "key_id",
                principalTable: "steam_keys",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
