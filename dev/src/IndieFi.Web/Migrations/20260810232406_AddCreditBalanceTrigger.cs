using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddCreditBalanceTrigger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The balance is derived from the ledger. Updating both in application code
            // made their agreement a matter of developer discipline and left a code path
            // that could change the balance without a ledger entry. Applying every delta
            // here removes that balance-writing path from the application entirely.
            //
            // SECURITY DEFINER is required for the next step, where the application role
            // loses permission to update users.credit_balance. It makes the function run
            // with its owner's privileges, so the application cannot update the balance
            // directly while a ledger insert can still make this trigger update it.
            // SET search_path is the matching safeguard: it fixes name resolution to the
            // public schema so caller-controlled schemas cannot substitute objects while
            // the function is running with its owner's privileges.
            //
            // This is an AFTER INSERT trigger because the balance follows an accepted
            // ledger row. PostgreSQL ignores an AFTER trigger's return value, hence NULL.
            // If the update would make the balance negative, the existing
            // CHECK (credit_balance >= 0) raises an exception and rolls back the update
            // and ledger insert together, exposing insufficient balance as an exception.
            migrationBuilder.Sql("""
                CREATE FUNCTION apply_credit_delta() RETURNS trigger
                    LANGUAGE plpgsql
                    SECURITY DEFINER
                    SET search_path = public
                AS $$
                BEGIN
                    UPDATE users SET credit_balance = credit_balance + NEW.delta WHERE id = NEW.user_id;
                    RETURN NULL;
                END;
                $$;

                CREATE TRIGGER credit_ledger_maintains_balance
                AFTER INSERT ON credit_ledger_entries
                FOR EACH ROW EXECUTE FUNCTION apply_credit_delta();
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER credit_ledger_maintains_balance ON credit_ledger_entries;
                DROP FUNCTION apply_credit_delta();
                """);

        }
    }
}
