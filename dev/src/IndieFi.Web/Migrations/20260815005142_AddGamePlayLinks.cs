using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddGamePlayLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "game_play_links",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    game_id = table.Column<long>(type: "bigint", nullable: false),
                    display_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    target_platform = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_game_play_links", x => x.id);
                    table.CheckConstraint("CK_game_play_links_display_name_not_blank", "length(btrim(display_name)) > 0");
                    table.CheckConstraint("CK_game_play_links_provider", "provider IN ('Steam', 'ItchIo', 'DeveloperWebsite', 'Other')");
                    table.CheckConstraint("CK_game_play_links_sort_order", "sort_order >= 0 AND sort_order < 5");
                    table.CheckConstraint("CK_game_play_links_target_platform", "target_platform IS NULL OR target_platform IN ('Windows', 'MacOS', 'Linux', 'Web', 'Android', 'Ios', 'Multiple', 'Other')");
                    table.CheckConstraint("CK_game_play_links_url_not_blank", "length(btrim(url)) > 0");
                    table.ForeignKey(
                        name: "FK_game_play_links_games_game_id",
                        column: x => x.game_id,
                        principalTable: "games",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_game_play_links_game_id_sort_order",
                table: "game_play_links",
                columns: new[] { "game_id", "sort_order" },
                unique: true);

            migrationBuilder.Sql(
                """
                GRANT SELECT, INSERT, UPDATE, DELETE
                    ON game_play_links
                    TO indiefi_app;
                GRANT USAGE, SELECT
                    ON SEQUENCE game_play_links_id_seq
                    TO indiefi_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "game_play_links");
        }
    }
}
