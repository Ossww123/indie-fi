using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class LinkFeedbackCreditOrigins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_feedback_submissions_review_assignments_assignment_id",
                table: "feedback_submissions");

            migrationBuilder.DropForeignKey(
                name: "FK_review_requests_credit_ledger_entries_refund_entry_id",
                table: "review_requests");

            migrationBuilder.DropForeignKey(
                name: "FK_review_requests_credit_ledger_entries_spend_entry_id",
                table: "review_requests");

            migrationBuilder.AddColumn<long>(
                name: "earn_entry_id",
                table: "feedback_submissions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "reviewer_id",
                table: "feedback_submissions",
                type: "bigint",
                nullable: true);

            // The former submission SQL created one Earn row per submission. Match each
            // reviewer's historical rows in stable creation order before making the links
            // required. Failing the migration is safer than silently inventing an origin.
            migrationBuilder.Sql(
                """
                WITH ranked_submissions AS (
                    SELECT submission.id,
                           assignment.reviewer_id,
                           row_number() OVER (
                               PARTITION BY assignment.reviewer_id
                               ORDER BY submission.created_at, submission.id) AS sequence
                      FROM feedback_submissions AS submission
                      JOIN review_assignments AS assignment
                        ON assignment.id = submission.assignment_id
                ),
                ranked_earn_entries AS (
                    SELECT ledger.id,
                           ledger.user_id,
                           row_number() OVER (
                               PARTITION BY ledger.user_id
                               ORDER BY ledger.created_at, ledger.id) AS sequence
                      FROM credit_ledger_entries AS ledger
                     WHERE ledger.kind = 'Earn'
                )
                UPDATE feedback_submissions AS submission
                   SET reviewer_id = ranked_submission.reviewer_id,
                       earn_entry_id = ranked_earn.id
                  FROM ranked_submissions AS ranked_submission
                  JOIN ranked_earn_entries AS ranked_earn
                    ON ranked_earn.user_id = ranked_submission.reviewer_id
                   AND ranked_earn.sequence = ranked_submission.sequence
                 WHERE submission.id = ranked_submission.id;

                DO $migration$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                          FROM feedback_submissions
                         WHERE reviewer_id IS NULL OR earn_entry_id IS NULL)
                    THEN
                        RAISE EXCEPTION
                            'Cannot link every feedback submission to its reviewer Earn entry';
                    END IF;
                END
                $migration$;
                """);

            migrationBuilder.AlterColumn<long>(
                name: "earn_entry_id",
                table: "feedback_submissions",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "reviewer_id",
                table: "feedback_submissions",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "kind",
                table: "credit_ledger_entries",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_review_assignments_id_reviewer_id",
                table: "review_assignments",
                columns: new[] { "id", "reviewer_id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_credit_ledger_entries_id_user_id",
                table: "credit_ledger_entries",
                columns: new[] { "id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_review_requests_refund_entry_id_requester_id",
                table: "review_requests",
                columns: new[] { "refund_entry_id", "requester_id" });

            migrationBuilder.CreateIndex(
                name: "IX_review_requests_spend_entry_id_requester_id",
                table: "review_requests",
                columns: new[] { "spend_entry_id", "requester_id" });

            migrationBuilder.CreateIndex(
                name: "IX_feedback_submissions_assignment_id_reviewer_id",
                table: "feedback_submissions",
                columns: new[] { "assignment_id", "reviewer_id" });

            migrationBuilder.CreateIndex(
                name: "IX_feedback_submissions_earn_entry_id",
                table: "feedback_submissions",
                column: "earn_entry_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_feedback_submissions_earn_entry_id_reviewer_id",
                table: "feedback_submissions",
                columns: new[] { "earn_entry_id", "reviewer_id" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_credit_ledger_entries_kind",
                table: "credit_ledger_entries",
                sql: "kind IN ('Grant', 'Spend', 'Earn', 'Refund')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_credit_ledger_entries_kind_delta",
                table: "credit_ledger_entries",
                sql: "kind NOT IN ('Grant', 'Spend', 'Earn', 'Refund') OR (kind = 'Grant' AND delta > 0) OR (kind = 'Spend' AND delta = -1) OR (kind = 'Earn' AND delta = 1) OR (kind = 'Refund' AND delta = 1)");

            migrationBuilder.AddForeignKey(
                name: "FK_feedback_submissions_credit_ledger_entries_earn_entry_id_re~",
                table: "feedback_submissions",
                columns: new[] { "earn_entry_id", "reviewer_id" },
                principalTable: "credit_ledger_entries",
                principalColumns: new[] { "id", "user_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_feedback_submissions_review_assignments_assignment_id_revie~",
                table: "feedback_submissions",
                columns: new[] { "assignment_id", "reviewer_id" },
                principalTable: "review_assignments",
                principalColumns: new[] { "id", "reviewer_id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_review_requests_credit_ledger_entries_refund_entry_id_reque~",
                table: "review_requests",
                columns: new[] { "refund_entry_id", "requester_id" },
                principalTable: "credit_ledger_entries",
                principalColumns: new[] { "id", "user_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_review_requests_credit_ledger_entries_spend_entry_id_reques~",
                table: "review_requests",
                columns: new[] { "spend_entry_id", "requester_id" },
                principalTable: "credit_ledger_entries",
                principalColumns: new[] { "id", "user_id" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_feedback_submissions_credit_ledger_entries_earn_entry_id_re~",
                table: "feedback_submissions");

            migrationBuilder.DropForeignKey(
                name: "FK_feedback_submissions_review_assignments_assignment_id_revie~",
                table: "feedback_submissions");

            migrationBuilder.DropForeignKey(
                name: "FK_review_requests_credit_ledger_entries_refund_entry_id_reque~",
                table: "review_requests");

            migrationBuilder.DropForeignKey(
                name: "FK_review_requests_credit_ledger_entries_spend_entry_id_reques~",
                table: "review_requests");

            migrationBuilder.DropIndex(
                name: "IX_review_requests_refund_entry_id_requester_id",
                table: "review_requests");

            migrationBuilder.DropIndex(
                name: "IX_review_requests_spend_entry_id_requester_id",
                table: "review_requests");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_review_assignments_id_reviewer_id",
                table: "review_assignments");

            migrationBuilder.DropIndex(
                name: "IX_feedback_submissions_assignment_id_reviewer_id",
                table: "feedback_submissions");

            migrationBuilder.DropIndex(
                name: "IX_feedback_submissions_earn_entry_id",
                table: "feedback_submissions");

            migrationBuilder.DropIndex(
                name: "IX_feedback_submissions_earn_entry_id_reviewer_id",
                table: "feedback_submissions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_credit_ledger_entries_id_user_id",
                table: "credit_ledger_entries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_credit_ledger_entries_kind",
                table: "credit_ledger_entries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_credit_ledger_entries_kind_delta",
                table: "credit_ledger_entries");

            migrationBuilder.DropColumn(
                name: "earn_entry_id",
                table: "feedback_submissions");

            migrationBuilder.DropColumn(
                name: "reviewer_id",
                table: "feedback_submissions");

            migrationBuilder.AlterColumn<string>(
                name: "kind",
                table: "credit_ledger_entries",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16);

            migrationBuilder.AddForeignKey(
                name: "FK_feedback_submissions_review_assignments_assignment_id",
                table: "feedback_submissions",
                column: "assignment_id",
                principalTable: "review_assignments",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_review_requests_credit_ledger_entries_refund_entry_id",
                table: "review_requests",
                column: "refund_entry_id",
                principalTable: "credit_ledger_entries",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "FK_review_requests_credit_ledger_entries_spend_entry_id",
                table: "review_requests",
                column: "spend_entry_id",
                principalTable: "credit_ledger_entries",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
