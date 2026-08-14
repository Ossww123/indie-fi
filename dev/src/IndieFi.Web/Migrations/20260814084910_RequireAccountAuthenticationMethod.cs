using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class RequireAccountAuthenticationMethod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_external_logins_provider_user_id_not_blank",
                table: "external_logins",
                sql: "length(btrim(provider_user_id)) > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_external_logins_supported_provider",
                table: "external_logins",
                sql: "provider = 'Google'");

            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION enforce_user_authentication_method()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                DECLARE
                    candidate_user_id bigint;
                    candidate_password_hash text;
                    candidate_user_ids bigint[];
                BEGIN
                    IF TG_TABLE_NAME = 'users' THEN
                        candidate_user_ids := ARRAY[NEW.id];
                    ELSIF TG_OP = 'DELETE' THEN
                        candidate_user_ids := ARRAY[OLD.user_id];
                    ELSIF TG_OP = 'UPDATE' THEN
                        candidate_user_ids := ARRAY[OLD.user_id, NEW.user_id];
                    ELSE
                        candidate_user_ids := ARRAY[NEW.user_id];
                    END IF;

                    FOR candidate_user_id IN
                        SELECT DISTINCT item
                          FROM unnest(candidate_user_ids) AS item
                         ORDER BY item
                    LOOP
                        candidate_password_hash := NULL;
                        SELECT account.password_hash
                          INTO candidate_password_hash
                          FROM users AS account
                         WHERE account.id = candidate_user_id
                           FOR UPDATE;

                        IF NOT FOUND THEN
                            CONTINUE;
                        END IF;

                        IF candidate_password_hash IS NULL
                           AND NOT EXISTS (
                               SELECT 1
                                 FROM external_logins AS login
                                WHERE login.user_id = candidate_user_id
                           ) THEN
                            RAISE EXCEPTION USING
                                ERRCODE = '23514',
                                CONSTRAINT = 'CT_users_authentication_method_required',
                                MESSAGE = 'A user must have a password or an external login.';
                        END IF;
                    END LOOP;

                    RETURN NULL;
                END;
                $$;

                CREATE CONSTRAINT TRIGGER users_require_authentication_method
                AFTER INSERT OR UPDATE OF password_hash ON users
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW
                EXECUTE FUNCTION enforce_user_authentication_method();

                CREATE CONSTRAINT TRIGGER external_logins_preserve_authentication_method
                AFTER INSERT OR UPDATE OR DELETE ON external_logins
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW
                EXECUTE FUNCTION enforce_user_authentication_method();

                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                          FROM users AS account
                         WHERE account.password_hash IS NULL
                           AND NOT EXISTS (
                               SELECT 1
                                 FROM external_logins AS login
                                WHERE login.user_id = account.id
                           )
                    ) THEN
                        RAISE EXCEPTION USING
                            ERRCODE = '23514',
                            CONSTRAINT = 'CT_users_authentication_method_required',
                            MESSAGE = 'Existing users without an authentication method were found.';
                    END IF;
                END;
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS external_logins_preserve_authentication_method
                    ON external_logins;
                DROP TRIGGER IF EXISTS users_require_authentication_method
                    ON users;
                DROP FUNCTION IF EXISTS enforce_user_authentication_method();
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_external_logins_provider_user_id_not_blank",
                table: "external_logins");

            migrationBuilder.DropCheckConstraint(
                name: "CK_external_logins_supported_provider",
                table: "external_logins");
        }
    }
}
