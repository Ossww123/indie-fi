using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddKeyDropOpenNotificationStage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "open_notification_staged_at",
                table: "key_drops",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_key_drops_opens_at",
                table: "key_drops",
                column: "opens_at",
                filter: "open_notification_staged_at IS NULL AND closed_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_key_drops_opens_at",
                table: "key_drops");

            migrationBuilder.DropColumn(
                name: "open_notification_staged_at",
                table: "key_drops");
        }
    }
}
