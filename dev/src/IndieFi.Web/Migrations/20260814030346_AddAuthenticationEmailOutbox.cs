using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddAuthenticationEmailOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "authentication_email_outbox_messages",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    payload_ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    payload_nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    payload_authentication_tag = table.Column<byte[]>(type: "bytea", nullable: false),
                    payload_key_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_authentication_email_outbox_messages", x => x.id);
                    table.CheckConstraint("CK_authentication_email_outbox_messages_kind", "kind IN ('EmailConfirmation')");
                    table.CheckConstraint("CK_authentication_email_outbox_messages_lifetime", "expires_at > created_at");
                    table.CheckConstraint("CK_authentication_email_outbox_messages_payload_shape", "octet_length(payload_ciphertext) > 0 AND octet_length(payload_nonce) = 12 AND octet_length(payload_authentication_tag) = 16");
                    table.ForeignKey(
                        name: "FK_authentication_email_outbox_messages_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_authentication_email_outbox_messages_expires_at",
                table: "authentication_email_outbox_messages",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_authentication_email_outbox_messages_user_id_kind",
                table: "authentication_email_outbox_messages",
                columns: new[] { "user_id", "kind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "authentication_email_outbox_messages");
        }
    }
}
