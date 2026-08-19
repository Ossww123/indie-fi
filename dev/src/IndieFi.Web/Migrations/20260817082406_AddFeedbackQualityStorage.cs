using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddFeedbackQualityStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_feedback_submissions_id_reviewer_id",
                table: "feedback_submissions",
                columns: new[] { "id", "reviewer_id" });

            migrationBuilder.CreateTable(
                name: "feedback_ratings",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    feedback_submission_id = table.Column<long>(type: "bigint", nullable: false),
                    rater_id = table.Column<long>(type: "bigint", nullable: false),
                    reviewer_id = table.Column<long>(type: "bigint", nullable: false),
                    score = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feedback_ratings", x => x.id);
                    table.CheckConstraint("CK_feedback_ratings_distinct_users", "rater_id <> reviewer_id");
                    table.CheckConstraint("CK_feedback_ratings_reason", "reason IS NULL OR (length(reason) > 0 AND reason = btrim(reason))");
                    table.CheckConstraint("CK_feedback_ratings_revision", "revision > 0");
                    table.CheckConstraint("CK_feedback_ratings_score", "score BETWEEN 1 AND 5");
                    table.CheckConstraint("CK_feedback_ratings_timestamps", "updated_at >= created_at");
                    table.ForeignKey(
                        name: "FK_feedback_ratings_feedback_submission_reviewer",
                        columns: x => new { x.feedback_submission_id, x.reviewer_id },
                        principalTable: "feedback_submissions",
                        principalColumns: new[] { "id", "reviewer_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_feedback_ratings_users_rater_id",
                        column: x => x.rater_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_feedback_ratings_users_reviewer_id",
                        column: x => x.reviewer_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "reviewer_reputations",
                columns: table => new
                {
                    reviewer_id = table.Column<long>(type: "bigint", nullable: false),
                    completed_review_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    expired_assignment_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    rating_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    rating_score_sum = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    revision = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reviewer_reputations", x => x.reviewer_id);
                    table.CheckConstraint("CK_reviewer_reputations_counts", "completed_review_count >= 0 AND expired_assignment_count >= 0 AND rating_count >= 0 AND rating_score_sum >= 0");
                    table.CheckConstraint("CK_reviewer_reputations_rating_aggregate", "(rating_count = 0 AND rating_score_sum = 0) OR (rating_count > 0 AND rating_score_sum::bigint >= rating_count::bigint AND rating_score_sum::bigint <= rating_count::bigint * 5)");
                    table.CheckConstraint("CK_reviewer_reputations_revision", "revision > 0");
                    table.ForeignKey(
                        name: "FK_reviewer_reputations_users_reviewer_id",
                        column: x => x.reviewer_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_feedback_ratings_feedback_submission_id",
                table: "feedback_ratings",
                column: "feedback_submission_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_feedback_ratings_feedback_submission_id_reviewer_id",
                table: "feedback_ratings",
                columns: new[] { "feedback_submission_id", "reviewer_id" });

            migrationBuilder.CreateIndex(
                name: "IX_feedback_ratings_rater_id",
                table: "feedback_ratings",
                column: "rater_id");

            migrationBuilder.CreateIndex(
                name: "IX_feedback_ratings_reviewer_id",
                table: "feedback_ratings",
                column: "reviewer_id");

            migrationBuilder.Sql(
                """
                CREATE FUNCTION validate_feedback_rating_owner()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                DECLARE
                    expected_reviewer_id bigint;
                    expected_rater_id bigint;
                BEGIN
                    SELECT submission.reviewer_id, request.requester_id
                      INTO expected_reviewer_id, expected_rater_id
                      FROM feedback_submissions AS submission
                      JOIN review_assignments AS assignment
                        ON assignment.id = submission.assignment_id
                      JOIN review_requests AS request
                        ON request.id = assignment.request_id
                     WHERE submission.id = NEW.feedback_submission_id;

                    -- Missing feedback and a copied reviewer mismatch are handled by the
                    -- composite foreign key, which provides the more precise failure.
                    IF NOT FOUND
                        OR expected_reviewer_id <> NEW.reviewer_id
                        OR NEW.rater_id = NEW.reviewer_id THEN
                        RETURN NEW;
                    END IF;

                    IF expected_rater_id <> NEW.rater_id THEN
                        RAISE EXCEPTION 'Only the game owner can rate this feedback.'
                            USING ERRCODE = '23514',
                                  CONSTRAINT = 'CK_feedback_ratings_rater_owns_feedback';
                    END IF;

                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER feedback_ratings_validate_owner
                BEFORE INSERT ON feedback_ratings
                FOR EACH ROW
                EXECUTE FUNCTION validate_feedback_rating_owner();

                REVOKE ALL ON feedback_ratings FROM indiefi_app;
                GRANT SELECT ON feedback_ratings TO indiefi_app;
                GRANT INSERT (
                    feedback_submission_id,
                    rater_id,
                    reviewer_id,
                    score,
                    reason)
                    ON feedback_ratings TO indiefi_app;
                GRANT UPDATE (score, reason, revision, updated_at)
                    ON feedback_ratings TO indiefi_app;
                GRANT USAGE, SELECT ON SEQUENCE feedback_ratings_id_seq TO indiefi_app;

                REVOKE ALL ON reviewer_reputations FROM indiefi_app;
                GRANT SELECT ON reviewer_reputations TO indiefi_app;
                GRANT INSERT (
                    reviewer_id,
                    completed_review_count,
                    expired_assignment_count,
                    rating_count,
                    rating_score_sum)
                    ON reviewer_reputations TO indiefi_app;
                GRANT UPDATE (
                    completed_review_count,
                    expired_assignment_count,
                    rating_count,
                    rating_score_sum,
                    revision,
                    updated_at)
                    ON reviewer_reputations TO indiefi_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "feedback_ratings");

            migrationBuilder.DropTable(
                name: "reviewer_reputations");

            migrationBuilder.Sql("DROP FUNCTION validate_feedback_rating_owner();");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_feedback_submissions_id_reviewer_id",
                table: "feedback_submissions");
        }
    }
}
