using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddApplicationRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Create the application role here so test containers and deployed servers
            // receive the same permission shape from the same migration history.
            // The password is deliberately environment-specific and is never stored here.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'indiefi_app') THEN
                        CREATE ROLE indiefi_app LOGIN;
                    END IF;
                END
                $$;

                GRANT USAGE ON SCHEMA public TO indiefi_app;

                GRANT SELECT, INSERT, UPDATE, DELETE
                    ON ALL TABLES IN SCHEMA public
                    TO indiefi_app;

                -- ALL TABLES covers only tables that exist when this migration runs.
                -- Default privileges also grant access to tables created later.
                ALTER DEFAULT PRIVILEGES IN SCHEMA public
                    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO indiefi_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER DEFAULT PRIVILEGES IN SCHEMA public
                    REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM indiefi_app;

                REVOKE SELECT, INSERT, UPDATE, DELETE
                    ON ALL TABLES IN SCHEMA public
                    FROM indiefi_app;

                REVOKE USAGE ON SCHEMA public FROM indiefi_app;

                DROP ROLE IF EXISTS indiefi_app;
                """);
        }
    }
}
