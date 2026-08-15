using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddUserFollows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_follows",
                columns: table => new
                {
                    follower_id = table.Column<long>(type: "bigint", nullable: false),
                    followed_user_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_follows", x => new { x.follower_id, x.followed_user_id });
                    table.CheckConstraint("CK_user_follows_not_self", "follower_id <> followed_user_id");
                    table.ForeignKey(
                        name: "FK_user_follows_users_followed_user_id",
                        column: x => x.followed_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_follows_users_follower_id",
                        column: x => x.follower_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_follows_followed_user_id",
                table: "user_follows",
                column: "followed_user_id");

            migrationBuilder.Sql(
                """
                REVOKE ALL ON user_follows FROM indiefi_app;
                GRANT SELECT, INSERT, DELETE
                    ON user_follows
                    TO indiefi_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_follows");
        }
    }
}
