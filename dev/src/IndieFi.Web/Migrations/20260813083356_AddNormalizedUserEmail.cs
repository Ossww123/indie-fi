using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddNormalizedUserEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "normalized_email",
                table: "users",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE users
                SET normalized_email = upper(btrim(email));

                DO $migration$
                BEGIN
                    IF EXISTS (
                        SELECT normalized_email
                        FROM users
                        GROUP BY normalized_email
                        HAVING count(*) > 1
                    ) THEN
                        RAISE EXCEPTION 'Cannot normalize user emails because case-insensitive duplicates exist.';
                    END IF;
                END
                $migration$;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "normalized_email",
                table: "users",
                type: "character varying(320)",
                maxLength: 320,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(320)",
                oldMaxLength: 320,
                oldNullable: true);

            // The application role intentionally has column-level INSERT permission
            // on users, so every new writable column must be granted explicitly.
            migrationBuilder.Sql(
                "GRANT INSERT (normalized_email) ON users TO indiefi_app;");

            migrationBuilder.CreateIndex(
                name: "IX_users_normalized_email",
                table: "users",
                column: "normalized_email",
                unique: true);

            migrationBuilder.DropIndex(
                name: "IX_users_email",
                table: "users");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "REVOKE INSERT (normalized_email) ON users FROM indiefi_app;");

            migrationBuilder.DropIndex(
                name: "IX_users_normalized_email",
                table: "users");

            migrationBuilder.CreateIndex(
                name: "IX_users_email",
                table: "users",
                column: "email",
                unique: true);

            migrationBuilder.DropColumn(
                name: "normalized_email",
                table: "users");
        }
    }
}
