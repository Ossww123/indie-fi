using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddUserProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_profiles",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    nickname = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    normalized_nickname = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    biography = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, defaultValue: ""),
                    nickname_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_profiles", x => x.user_id);
                    table.CheckConstraint("CK_user_profiles_biography_length", "char_length(biography) <= 500");
                    table.CheckConstraint("CK_user_profiles_nickname_shape", "(nickname IS NULL AND normalized_nickname IS NULL) OR (nickname IS NOT NULL AND normalized_nickname IS NOT NULL AND char_length(nickname) BETWEEN 2 AND 20 AND char_length(normalized_nickname) BETWEEN 2 AND 20)");
                    table.CheckConstraint("CK_user_profiles_timestamps", "updated_at >= created_at AND (nickname_changed_at IS NULL OR nickname_changed_at >= created_at)");
                    table.ForeignKey(
                        name: "FK_user_profiles_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_profiles_normalized_nickname",
                table: "user_profiles",
                column: "normalized_nickname",
                unique: true);

            migrationBuilder.Sql(
                """
                GRANT SELECT, INSERT, UPDATE, DELETE
                    ON user_profiles
                    TO indiefi_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_profiles");
        }
    }
}
