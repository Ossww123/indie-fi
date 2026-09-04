using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddKeyDropStateVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "state_version",
                table: "key_drops",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddCheckConstraint(
                name: "CK_key_drops_state_version_nonnegative",
                table: "key_drops",
                sql: "state_version >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_key_drops_state_version_nonnegative",
                table: "key_drops");

            migrationBuilder.DropColumn(
                name: "state_version",
                table: "key_drops");
        }
    }
}
