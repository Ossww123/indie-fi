using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddKeyDropEligibilityRequirements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "minimum_account_age_days",
                table: "key_drops",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "minimum_completed_review_count",
                table: "key_drops",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_key_drops_minimum_account_age_days_range",
                table: "key_drops",
                sql: "minimum_account_age_days IS NULL OR minimum_account_age_days BETWEEN 1 AND 365");

            migrationBuilder.AddCheckConstraint(
                name: "CK_key_drops_minimum_completed_review_count_range",
                table: "key_drops",
                sql: "minimum_completed_review_count IS NULL OR minimum_completed_review_count BETWEEN 1 AND 100");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_key_drops_minimum_account_age_days_range",
                table: "key_drops");

            migrationBuilder.DropCheckConstraint(
                name: "CK_key_drops_minimum_completed_review_count_range",
                table: "key_drops");

            migrationBuilder.DropColumn(
                name: "minimum_account_age_days",
                table: "key_drops");

            migrationBuilder.DropColumn(
                name: "minimum_completed_review_count",
                table: "key_drops");
        }
    }
}
