using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewRequestRefund : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "refund_entry_id",
                table: "review_requests",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_review_requests_refund_entry_id",
                table: "review_requests",
                column: "refund_entry_id",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_review_requests_credit_ledger_entries_refund_entry_id",
                table: "review_requests",
                column: "refund_entry_id",
                principalTable: "credit_ledger_entries",
                principalColumn: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_review_requests_credit_ledger_entries_refund_entry_id",
                table: "review_requests");

            migrationBuilder.DropIndex(
                name: "IX_review_requests_refund_entry_id",
                table: "review_requests");

            migrationBuilder.DropColumn(
                name: "refund_entry_id",
                table: "review_requests");
        }
    }
}
