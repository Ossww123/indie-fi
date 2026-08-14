using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class LimitUnresolvedEmailConfirmationTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // PostgreSQL requires UPDATE privilege on at least one users column before
            // SELECT ... FOR UPDATE may coordinate token issuers. This is also the only
            // users column the email confirmation flow will change when a token is consumed.
            migrationBuilder.Sql(
                "GRANT UPDATE (email_confirmed_at) ON users TO indiefi_app;");

            migrationBuilder.DropIndex(
                name: "IX_email_confirmation_tokens_user_id",
                table: "email_confirmation_tokens");

            migrationBuilder.CreateIndex(
                name: "IX_email_confirmation_tokens_user_id_unresolved",
                table: "email_confirmation_tokens",
                column: "user_id",
                unique: true,
                filter: "used_at IS NULL AND revoked_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "REVOKE UPDATE (email_confirmed_at) ON users FROM indiefi_app;");

            migrationBuilder.DropIndex(
                name: "IX_email_confirmation_tokens_user_id_unresolved",
                table: "email_confirmation_tokens");

            migrationBuilder.CreateIndex(
                name: "IX_email_confirmation_tokens_user_id",
                table: "email_confirmation_tokens",
                column: "user_id");
        }
    }
}
