using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class GrantAccountStatusTransitionPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // users intentionally has no table-level UPDATE permission. Account-state
            // transitions also update session_version, which the application already has,
            // so grant only the two newly required state columns here.
            migrationBuilder.Sql(
                "GRANT UPDATE (account_status, suspended_until) ON users TO indiefi_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "REVOKE UPDATE (account_status, suspended_until) ON users FROM indiefi_app;");
        }
    }
}
