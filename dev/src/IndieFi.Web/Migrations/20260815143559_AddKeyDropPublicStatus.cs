using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddKeyDropPublicStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "closed_at",
                table: "key_drops",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_key_drops_total_keys_nonnegative",
                table: "key_drops",
                sql: "total_keys >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_key_drops_total_keys_nonnegative",
                table: "key_drops");

            migrationBuilder.DropColumn(
                name: "closed_at",
                table: "key_drops");
        }
    }
}
