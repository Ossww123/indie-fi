using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class GrantPasswordChangePermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // users intentionally has no table-level UPDATE permission. Password changes
            // need only these two columns, and the existing credit and account-state
            // columns must remain protected from direct application writes.
            migrationBuilder.Sql(
                "GRANT UPDATE (password_hash, session_version) ON users TO indiefi_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "REVOKE UPDATE (password_hash, session_version) ON users FROM indiefi_app;");
        }
    }
}
