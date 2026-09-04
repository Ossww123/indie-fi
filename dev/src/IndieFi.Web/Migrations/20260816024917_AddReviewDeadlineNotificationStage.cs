using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewDeadlineNotificationStage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deadline_notification_staged_at",
                table: "review_assignments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_review_assignments_status_leased_until",
                table: "review_assignments",
                columns: new[] { "status", "leased_until" },
                filter: "deadline_notification_staged_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_review_assignments_status_leased_until",
                table: "review_assignments");

            migrationBuilder.DropColumn(
                name: "deadline_notification_staged_at",
                table: "review_assignments");
        }
    }
}
