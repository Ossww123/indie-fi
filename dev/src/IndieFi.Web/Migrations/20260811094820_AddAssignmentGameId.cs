using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddAssignmentGameId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_review_assignments_review_requests_request_id",
                table: "review_assignments");

            migrationBuilder.DropIndex(
                name: "IX_review_assignments_reviewer_id",
                table: "review_assignments");

            // Written by hand: the generated version added this column as NOT NULL DEFAULT 0,
            // which gives every existing assignment a game that does not exist and makes the
            // composite foreign key below fail with 23503. The column therefore arrives
            // nullable, is filled from each assignment's own request, and is tightened after.
            migrationBuilder.AddColumn<long>(
                name: "game_id",
                table: "review_assignments",
                type: "bigint",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE review_assignments a
                   SET game_id = r.game_id
                  FROM review_requests r
                 WHERE r.id = a.request_id
                """);

            // The old* arguments are required, not decoration: without them EF assumes the
            // column was already NOT NULL, finds no difference, and emits no SQL at all.
            migrationBuilder.AlterColumn<long>(
                name: "game_id",
                table: "review_assignments",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_review_requests_id_game_id",
                table: "review_requests",
                columns: new[] { "id", "game_id" });

            migrationBuilder.CreateIndex(
                name: "IX_review_assignments_request_id_game_id",
                table: "review_assignments",
                columns: new[] { "request_id", "game_id" });

            migrationBuilder.CreateIndex(
                name: "IX_review_assignments_reviewer_id_game_id",
                table: "review_assignments",
                columns: new[] { "reviewer_id", "game_id" },
                unique: true,
                filter: "status <> 'Expired'");

            migrationBuilder.AddForeignKey(
                name: "FK_review_assignments_review_requests_request_id_game_id",
                table: "review_assignments",
                columns: new[] { "request_id", "game_id" },
                principalTable: "review_requests",
                principalColumns: new[] { "id", "game_id" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_review_assignments_review_requests_request_id_game_id",
                table: "review_assignments");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_review_requests_id_game_id",
                table: "review_requests");

            migrationBuilder.DropIndex(
                name: "IX_review_assignments_request_id_game_id",
                table: "review_assignments");

            migrationBuilder.DropIndex(
                name: "IX_review_assignments_reviewer_id_game_id",
                table: "review_assignments");

            migrationBuilder.DropColumn(
                name: "game_id",
                table: "review_assignments");

            migrationBuilder.CreateIndex(
                name: "IX_review_assignments_reviewer_id",
                table: "review_assignments",
                column: "reviewer_id");

            migrationBuilder.AddForeignKey(
                name: "FK_review_assignments_review_requests_request_id",
                table: "review_assignments",
                column: "request_id",
                principalTable: "review_requests",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
