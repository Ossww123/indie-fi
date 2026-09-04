using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddUserCreditBalance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "credit_balance",
                table: "users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Accounts created before this column existed already have ledger entries,
            // so the new column starts out disagreeing with them. Deriving it from the
            // ledger is what makes the two consistent from the first moment.
            migrationBuilder.Sql("""
                UPDATE users
                SET credit_balance = COALESCE((
                    SELECT SUM(delta)
                    FROM credit_ledger_entries
                    WHERE credit_ledger_entries.user_id = users.id), 0);
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_users_credit_balance_not_negative",
                table: "users",
                sql: "credit_balance >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_users_credit_balance_not_negative",
                table: "users");

            migrationBuilder.DropColumn(
                name: "credit_balance",
                table: "users");
        }
    }
}
