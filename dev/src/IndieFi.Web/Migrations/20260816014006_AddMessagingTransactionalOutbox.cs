using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddMessagingTransactionalOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "message_inbox_states",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consumer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lock_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_version = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                    received = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    receive_count = table.Column<int>(type: "integer", nullable: false),
                    expiration_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    consumed = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    delivered = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_sequence_number = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_inbox_states", x => x.id);
                    table.UniqueConstraint("AK_message_inbox_states_message_id_consumer_id", x => new { x.message_id, x.consumer_id });
                });

            migrationBuilder.CreateTable(
                name: "message_outbox_states",
                columns: table => new
                {
                    outbox_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lock_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_version = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                    created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    delivered = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_sequence_number = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_outbox_states", x => x.outbox_id);
                });

            migrationBuilder.CreateTable(
                name: "message_outbox_messages",
                columns: table => new
                {
                    sequence_number = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    enqueue_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    sent_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    headers = table.Column<string>(type: "text", nullable: true),
                    properties = table.Column<string>(type: "text", nullable: true),
                    inbox_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    inbox_consumer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    outbox_id = table.Column<Guid>(type: "uuid", nullable: true),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_type = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    message_type = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    initiator_id = table.Column<Guid>(type: "uuid", nullable: true),
                    request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_address = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    destination_address = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    response_address = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    fault_address = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    expiration_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_outbox_messages", x => x.sequence_number);
                    table.ForeignKey(
                        name: "FK_message_outbox_messages_message_inbox_states_inbox_message_~",
                        columns: x => new { x.inbox_message_id, x.inbox_consumer_id },
                        principalTable: "message_inbox_states",
                        principalColumns: new[] { "message_id", "consumer_id" });
                    table.ForeignKey(
                        name: "FK_message_outbox_messages_message_outbox_states_outbox_id",
                        column: x => x.outbox_id,
                        principalTable: "message_outbox_states",
                        principalColumn: "outbox_id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_message_inbox_states_delivered",
                table: "message_inbox_states",
                column: "delivered");

            migrationBuilder.CreateIndex(
                name: "IX_message_outbox_messages_enqueue_time",
                table: "message_outbox_messages",
                column: "enqueue_time");

            migrationBuilder.CreateIndex(
                name: "IX_message_outbox_messages_expiration_time",
                table: "message_outbox_messages",
                column: "expiration_time");

            migrationBuilder.CreateIndex(
                name: "IX_message_outbox_messages_inbox_message_id_inbox_consumer_id_~",
                table: "message_outbox_messages",
                columns: new[] { "inbox_message_id", "inbox_consumer_id", "sequence_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_message_outbox_messages_outbox_id_sequence_number",
                table: "message_outbox_messages",
                columns: new[] { "outbox_id", "sequence_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_message_outbox_states_created",
                table: "message_outbox_states",
                column: "created");

            migrationBuilder.Sql(
                """
                REVOKE ALL ON message_inbox_states FROM indiefi_app;
                GRANT SELECT, INSERT, UPDATE, DELETE
                    ON message_inbox_states
                    TO indiefi_app;
                GRANT USAGE, SELECT
                    ON SEQUENCE message_inbox_states_id_seq
                    TO indiefi_app;

                REVOKE ALL ON message_outbox_messages FROM indiefi_app;
                GRANT SELECT, INSERT, UPDATE, DELETE
                    ON message_outbox_messages
                    TO indiefi_app;
                GRANT USAGE, SELECT
                    ON SEQUENCE message_outbox_messages_sequence_number_seq
                    TO indiefi_app;

                REVOKE ALL ON message_outbox_states FROM indiefi_app;
                GRANT SELECT, INSERT, UPDATE, DELETE
                    ON message_outbox_states
                    TO indiefi_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "message_outbox_messages");

            migrationBuilder.DropTable(
                name: "message_inbox_states");

            migrationBuilder.DropTable(
                name: "message_outbox_states");
        }
    }
}
