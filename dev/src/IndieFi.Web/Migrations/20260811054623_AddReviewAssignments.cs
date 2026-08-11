using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "review_requests",
                type: "text",
                nullable: false,
                defaultValue: "Queued");

            migrationBuilder.CreateTable(
                name: "review_assignments",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    request_id = table.Column<long>(type: "bigint", nullable: false),
                    reviewer_id = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    leased_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_assignments", x => x.id);
                    table.ForeignKey(
                        name: "FK_review_assignments_review_requests_request_id",
                        column: x => x.request_id,
                        principalTable: "review_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_review_assignments_users_reviewer_id",
                        column: x => x.reviewer_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_review_requests_queued",
                table: "review_requests",
                column: "id",
                filter: "status = 'Queued'");

            migrationBuilder.CreateIndex(
                name: "IX_review_assignments_request_id",
                table: "review_assignments",
                column: "request_id",
                unique: true,
                filter: "status <> 'Expired'");

            migrationBuilder.CreateIndex(
                name: "IX_review_assignments_reviewer_id",
                table: "review_assignments",
                column: "reviewer_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "review_assignments");

            migrationBuilder.DropIndex(
                name: "IX_review_requests_queued",
                table: "review_requests");

            migrationBuilder.DropColumn(
                name: "status",
                table: "review_requests");
        }
    }
}
