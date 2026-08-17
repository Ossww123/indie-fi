using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddGameDiscoveryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");

            migrationBuilder.Sql(
                """
                CREATE INDEX "IX_games_search_document_trgm"
                    ON games
                    USING GIN ((lower(title || ' ' || coalesce(description, ''))) gin_trgm_ops);
                """);

            migrationBuilder.Sql(
                """
                CREATE INDEX "IX_games_created_at_id_desc"
                    ON games (created_at DESC, id DESC);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS \"IX_games_created_at_id_desc\";");
            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS \"IX_games_search_document_trgm\";");

            // pg_trgm may be shared by another feature or an operator-created
            // index, so rolling this migration back must not drop the extension.
        }
    }
}
