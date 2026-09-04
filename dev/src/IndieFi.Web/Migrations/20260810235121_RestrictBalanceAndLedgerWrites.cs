using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class RestrictBalanceAndLedgerWrites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                -- Column privileges are additive in PostgreSQL: they cannot subtract from
                -- a table-level privilege. Revoke the table grants before granting only
                -- the columns the application is allowed to insert.
                REVOKE UPDATE ON users FROM indiefi_app;

                -- The application does not update users. If that changes, grant only the
                -- specific columns required by the new operation.
                REVOKE INSERT ON users FROM indiefi_app;
                GRANT INSERT (email, password_hash) ON users TO indiefi_app;

                -- The balance trigger runs only after INSERT. Updating a historical delta
                -- or deleting an entry would bypass it and split the balance from the ledger.
                REVOKE UPDATE, DELETE ON credit_ledger_entries FROM indiefi_app;

                -- The trigger function is SECURITY DEFINER, so it updates the balance with
                -- its owner's privileges. The application cannot update the balance directly,
                -- while inserting a ledger entry still moves the balance.
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                REVOKE INSERT (email, password_hash) ON users FROM indiefi_app;
                GRANT INSERT, UPDATE ON users TO indiefi_app;

                GRANT UPDATE, DELETE ON credit_ledger_entries TO indiefi_app;
                """);
        }
    }
}
