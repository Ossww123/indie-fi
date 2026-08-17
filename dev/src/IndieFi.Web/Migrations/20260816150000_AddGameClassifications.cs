using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddGameClassifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "genre_id",
                table: "games",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.CreateTable(
                name: "game_genres",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    display_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_game_genres", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "game_platforms",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    display_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_game_platforms", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "game_tags",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    display_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_game_tags", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "game_platform_assignments",
                columns: table => new
                {
                    game_id = table.Column<long>(type: "bigint", nullable: false),
                    platform_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_game_platform_assignments", x => new { x.game_id, x.platform_id });
                    table.ForeignKey(
                        name: "FK_game_platform_assignments_game_platforms_platform_id",
                        column: x => x.platform_id,
                        principalTable: "game_platforms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_game_platform_assignments_games_game_id",
                        column: x => x.game_id,
                        principalTable: "games",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "game_tag_assignments",
                columns: table => new
                {
                    game_id = table.Column<long>(type: "bigint", nullable: false),
                    tag_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_game_tag_assignments", x => new { x.game_id, x.tag_id });
                    table.ForeignKey(
                        name: "FK_game_tag_assignments_game_tags_tag_id",
                        column: x => x.tag_id,
                        principalTable: "game_tags",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_game_tag_assignments_games_game_id",
                        column: x => x.game_id,
                        principalTable: "games",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "game_genres",
                columns: new[] { "id", "code", "display_name", "is_active", "sort_order" },
                values: new object[,]
                {
                    { 1L, "unspecified", "미지정", false, 0 },
                    { 10L, "action", "액션", true, 10 },
                    { 11L, "adventure", "어드벤처", true, 20 },
                    { 12L, "role-playing", "롤플레잉(RPG)", true, 30 },
                    { 13L, "strategy", "전략", true, 40 },
                    { 14L, "simulation", "시뮬레이션", true, 50 },
                    { 15L, "puzzle", "퍼즐", true, 60 },
                    { 16L, "platformer", "플랫포머", true, 70 },
                    { 17L, "shooter", "슈팅", true, 80 },
                    { 18L, "racing", "레이싱", true, 90 },
                    { 19L, "sports", "스포츠", true, 100 },
                    { 20L, "fighting", "대전 격투", true, 110 },
                    { 21L, "rhythm", "리듬", true, 120 },
                    { 22L, "horror", "공포", true, 130 },
                    { 23L, "visual-novel", "비주얼 노벨", true, 140 },
                    { 24L, "roguelike", "로그라이크", true, 150 },
                    { 25L, "survival", "생존", true, 160 },
                    { 26L, "sandbox", "샌드박스", true, 170 },
                    { 27L, "card-board", "카드·보드", true, 180 },
                    { 28L, "casual", "캐주얼", true, 190 },
                    { 29L, "other", "기타", true, 200 }
                });

            migrationBuilder.InsertData(
                table: "game_platforms",
                columns: new[] { "id", "code", "display_name", "is_active", "sort_order" },
                values: new object[,]
                {
                    { 1L, "unspecified", "미지정", false, 0 },
                    { 10L, "windows", "Windows", true, 10 },
                    { 11L, "macos", "macOS", true, 20 },
                    { 12L, "linux", "Linux", true, 30 },
                    { 13L, "web", "Web", true, 40 },
                    { 14L, "android", "Android", true, 50 },
                    { 15L, "ios", "iOS", true, 60 },
                    { 16L, "steam-deck", "Steam Deck", true, 70 },
                    { 17L, "playstation-5", "PlayStation 5", true, 80 },
                    { 18L, "xbox-series", "Xbox Series X|S", true, 90 },
                    { 19L, "nintendo-switch", "Nintendo Switch", true, 100 },
                    { 20L, "virtual-reality", "VR", true, 110 },
                    { 21L, "other", "기타 플랫폼", true, 120 }
                });

            migrationBuilder.InsertData(
                table: "game_tags",
                columns: new[] { "id", "code", "display_name", "is_active", "sort_order" },
                values: new object[,]
                {
                    { 10L, "single-player", "싱글플레이", true, 10 },
                    { 11L, "multiplayer", "멀티플레이", true, 20 },
                    { 12L, "online-multiplayer", "온라인 멀티플레이", true, 30 },
                    { 13L, "local-multiplayer", "로컬 멀티플레이", true, 40 },
                    { 14L, "cooperative", "협동", true, 50 },
                    { 15L, "pvp", "PvP", true, 60 },
                    { 16L, "story-rich", "스토리 중심", true, 70 },
                    { 17L, "exploration", "탐험", true, 80 },
                    { 18L, "turn-based", "턴제", true, 90 },
                    { 19L, "real-time", "실시간", true, 100 },
                    { 20L, "procedural-generation", "절차적 생성", true, 110 },
                    { 21L, "replayable", "반복 플레이", true, 120 },
                    { 22L, "difficult", "높은 난이도", true, 130 },
                    { 23L, "relaxing", "편안한 플레이", true, 140 },
                    { 24L, "short", "짧은 플레이", true, 150 },
                    { 25L, "open-world", "오픈 월드", true, 160 },
                    { 26L, "2d", "2D", true, 170 },
                    { 27L, "3d", "3D", true, 180 },
                    { 28L, "pixel-art", "픽셀 아트", true, 190 },
                    { 29L, "hand-drawn", "손그림", true, 200 },
                    { 30L, "retro", "레트로", true, 210 },
                    { 31L, "atmospheric", "분위기 중심", true, 220 },
                    { 32L, "controller-support", "컨트롤러 지원", true, 230 },
                    { 33L, "keyboard-mouse", "키보드·마우스", true, 240 },
                    { 34L, "touch-controls", "터치 조작", true, 250 },
                    { 35L, "family-friendly", "가족 친화", true, 260 },
                    { 36L, "accessibility-options", "접근성 옵션", true, 270 },
                    { 37L, "experimental", "실험적", true, 280 }
                });

            // A legacy game has no platform information. Preserve that meaning
            // explicitly instead of guessing an active platform from its links.
            migrationBuilder.Sql(
                """
                INSERT INTO game_platform_assignments (game_id, platform_id)
                SELECT id, 1
                FROM games;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_games_genre_id",
                table: "games",
                column: "genre_id");

            migrationBuilder.CreateIndex(
                name: "IX_game_platform_assignments_platform_id",
                table: "game_platform_assignments",
                column: "platform_id");

            migrationBuilder.CreateIndex(
                name: "IX_game_tag_assignments_tag_id",
                table: "game_tag_assignments",
                column: "tag_id");

            migrationBuilder.AddForeignKey(
                name: "FK_games_game_genres_genre_id",
                table: "games",
                column: "genre_id",
                principalTable: "game_genres",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_games_game_genres_genre_id",
                table: "games");

            migrationBuilder.DropTable(
                name: "game_genres");

            migrationBuilder.DropTable(
                name: "game_platform_assignments");

            migrationBuilder.DropTable(
                name: "game_tag_assignments");

            migrationBuilder.DropTable(
                name: "game_platforms");

            migrationBuilder.DropTable(
                name: "game_tags");

            migrationBuilder.DropIndex(
                name: "IX_games_genre_id",
                table: "games");

            migrationBuilder.DropColumn(
                name: "genre_id",
                table: "games");
        }
    }
}
