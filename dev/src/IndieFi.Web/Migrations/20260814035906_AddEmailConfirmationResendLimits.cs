using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailConfirmationResendLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "email_confirmation_resend_attempts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: true),
                    request_ip_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    request_ip_hash_key_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_confirmation_resend_attempts", x => x.id);
                    table.CheckConstraint("CK_email_confirmation_resend_attempts_ip_hash_length", "octet_length(request_ip_hash) = 32");
                    table.ForeignKey(
                        name: "FK_email_confirmation_resend_attempts_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_email_confirmation_resend_attempts_created_at",
                table: "email_confirmation_resend_attempts",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_email_confirmation_resend_attempts_ip_created_at",
                table: "email_confirmation_resend_attempts",
                columns: new[] { "request_ip_hash_key_id", "request_ip_hash", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_email_confirmation_resend_attempts_user_id_created_at",
                table: "email_confirmation_resend_attempts",
                columns: new[] { "user_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "email_confirmation_resend_attempts");
        }
    }
}
