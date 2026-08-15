using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewStateTimestamps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "status",
                table: "review_requests",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Queued",
                oldClrType: typeof(string),
                oldType: "text",
                oldDefaultValue: "Queued");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "cancelled_at",
                table: "review_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "completed_at",
                table: "review_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "status",
                table: "review_assignments",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "completed_at",
                table: "review_assignments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "expired_at",
                table: "review_assignments",
                type: "timestamp with time zone",
                nullable: true);

            // Preserve rows created before terminal timestamps became part of the model. The
            // related submission/refund is the best available event time; expired assignments
            // fall back to the lease boundary without writing a future timestamp.
            migrationBuilder.Sql(
                """
                UPDATE review_assignments AS assignment
                   SET completed_at = COALESCE(
                       (SELECT submission.created_at
                          FROM feedback_submissions AS submission
                         WHERE submission.assignment_id = assignment.id),
                       assignment.created_at)
                 WHERE assignment.status = 'Completed';

                UPDATE review_assignments
                   SET expired_at = GREATEST(created_at, LEAST(leased_until, now()))
                 WHERE status = 'Expired';

                UPDATE review_requests AS request
                   SET completed_at = COALESCE(
                       (SELECT MIN(submission.created_at)
                          FROM review_assignments AS assignment
                          JOIN feedback_submissions AS submission
                            ON submission.assignment_id = assignment.id
                         WHERE assignment.request_id = request.id),
                       request.created_at)
                 WHERE request.status = 'Completed';

                UPDATE review_requests AS request
                   SET cancelled_at = COALESCE(
                       (SELECT ledger.created_at
                          FROM credit_ledger_entries AS ledger
                         WHERE ledger.id = request.refund_entry_id),
                       request.created_at)
                 WHERE request.status = 'Cancelled';
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_review_requests_status",
                table: "review_requests",
                sql: "status IN ('Queued', 'Assigned', 'Completed', 'Cancelled')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_review_requests_terminal_timestamps",
                table: "review_requests",
                sql: "(status IN ('Queued', 'Assigned') AND completed_at IS NULL AND cancelled_at IS NULL) OR (status = 'Completed' AND completed_at IS NOT NULL AND cancelled_at IS NULL) OR (status = 'Cancelled' AND cancelled_at IS NOT NULL AND completed_at IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_review_assignments_status",
                table: "review_assignments",
                sql: "status IN ('Active', 'Completed', 'Expired')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_review_assignments_terminal_timestamps",
                table: "review_assignments",
                sql: "(status = 'Active' AND completed_at IS NULL AND expired_at IS NULL) OR (status = 'Completed' AND completed_at IS NOT NULL AND expired_at IS NULL) OR (status = 'Expired' AND expired_at IS NOT NULL AND completed_at IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_review_requests_status",
                table: "review_requests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_review_requests_terminal_timestamps",
                table: "review_requests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_review_assignments_status",
                table: "review_assignments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_review_assignments_terminal_timestamps",
                table: "review_assignments");

            migrationBuilder.DropColumn(
                name: "cancelled_at",
                table: "review_requests");

            migrationBuilder.DropColumn(
                name: "completed_at",
                table: "review_requests");

            migrationBuilder.DropColumn(
                name: "completed_at",
                table: "review_assignments");

            migrationBuilder.DropColumn(
                name: "expired_at",
                table: "review_assignments");

            migrationBuilder.AlterColumn<string>(
                name: "status",
                table: "review_requests",
                type: "text",
                nullable: false,
                defaultValue: "Queued",
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16,
                oldDefaultValue: "Queued");

            migrationBuilder.AlterColumn<string>(
                name: "status",
                table: "review_assignments",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16);
        }
    }
}
