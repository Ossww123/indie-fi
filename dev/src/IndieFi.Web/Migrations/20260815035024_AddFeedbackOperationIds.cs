using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddFeedbackOperationIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_review_requests_requester_id",
                table: "review_requests");

            migrationBuilder.AddColumn<Guid>(
                name: "creation_operation_id",
                table: "review_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "claim_operation_id",
                table: "review_assignments",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE review_requests
                   SET creation_operation_id = gen_random_uuid();

                UPDATE review_assignments
                   SET claim_operation_id = gen_random_uuid();
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "creation_operation_id",
                table: "review_requests",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "claim_operation_id",
                table: "review_assignments",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_review_requests_requester_id_creation_operation_id",
                table: "review_requests",
                columns: new[] { "requester_id", "creation_operation_id" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_review_requests_creation_operation_id",
                table: "review_requests",
                sql: "creation_operation_id <> '00000000-0000-0000-0000-000000000000'::uuid");

            migrationBuilder.CreateIndex(
                name: "IX_review_assignments_reviewer_id_claim_operation_id",
                table: "review_assignments",
                columns: new[] { "reviewer_id", "claim_operation_id" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_review_assignments_claim_operation_id",
                table: "review_assignments",
                sql: "claim_operation_id <> '00000000-0000-0000-0000-000000000000'::uuid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_review_requests_requester_id_creation_operation_id",
                table: "review_requests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_review_requests_creation_operation_id",
                table: "review_requests");

            migrationBuilder.DropIndex(
                name: "IX_review_assignments_reviewer_id_claim_operation_id",
                table: "review_assignments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_review_assignments_claim_operation_id",
                table: "review_assignments");

            migrationBuilder.DropColumn(
                name: "creation_operation_id",
                table: "review_requests");

            migrationBuilder.DropColumn(
                name: "claim_operation_id",
                table: "review_assignments");

            migrationBuilder.CreateIndex(
                name: "IX_review_requests_requester_id",
                table: "review_requests",
                column: "requester_id");
        }
    }
}
