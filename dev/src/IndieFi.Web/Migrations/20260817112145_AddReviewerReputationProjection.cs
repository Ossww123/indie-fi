using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewerReputationProjection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE FUNCTION refresh_reviewer_reputation(target_reviewer_id bigint)
                RETURNS void
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    PERFORM pg_advisory_xact_lock(
                        hashtextextended(
                            'indiefi:reviewer-reputation:' || target_reviewer_id::text,
                            0));

                    INSERT INTO reviewer_reputations (
                        reviewer_id,
                        completed_review_count,
                        expired_assignment_count,
                        rating_count,
                        rating_score_sum)
                    SELECT users.id,
                           (
                               SELECT count(*)::integer
                                 FROM review_assignments AS assignment
                                WHERE assignment.reviewer_id = users.id
                                  AND assignment.status = 'Completed'
                           ),
                           (
                               SELECT count(*)::integer
                                 FROM review_assignments AS assignment
                                WHERE assignment.reviewer_id = users.id
                                  AND assignment.status = 'Expired'
                           ),
                           (
                               SELECT count(*)::integer
                                 FROM feedback_ratings AS rating
                                WHERE rating.reviewer_id = users.id
                           ),
                           (
                               SELECT coalesce(sum(rating.score), 0)::integer
                                 FROM feedback_ratings AS rating
                                WHERE rating.reviewer_id = users.id
                           )
                      FROM users
                     WHERE users.id = target_reviewer_id
                    ON CONFLICT (reviewer_id) DO UPDATE
                       SET completed_review_count = EXCLUDED.completed_review_count,
                           expired_assignment_count = EXCLUDED.expired_assignment_count,
                           rating_count = EXCLUDED.rating_count,
                           rating_score_sum = EXCLUDED.rating_score_sum,
                           revision = reviewer_reputations.revision + 1,
                           updated_at = now()
                     WHERE ROW(
                               reviewer_reputations.completed_review_count,
                               reviewer_reputations.expired_assignment_count,
                               reviewer_reputations.rating_count,
                               reviewer_reputations.rating_score_sum)
                           IS DISTINCT FROM ROW(
                               EXCLUDED.completed_review_count,
                               EXCLUDED.expired_assignment_count,
                               EXCLUDED.rating_count,
                               EXCLUDED.rating_score_sum);
                END;
                $function$;

                CREATE FUNCTION refresh_reviewer_reputation_from_source()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        PERFORM refresh_reviewer_reputation(OLD.reviewer_id);
                        RETURN OLD;
                    END IF;

                    IF TG_OP = 'INSERT' THEN
                        PERFORM refresh_reviewer_reputation(NEW.reviewer_id);
                        RETURN NEW;
                    END IF;

                    IF OLD.reviewer_id = NEW.reviewer_id THEN
                        PERFORM refresh_reviewer_reputation(NEW.reviewer_id);
                    ELSIF OLD.reviewer_id < NEW.reviewer_id THEN
                        PERFORM refresh_reviewer_reputation(OLD.reviewer_id);
                        PERFORM refresh_reviewer_reputation(NEW.reviewer_id);
                    ELSE
                        PERFORM refresh_reviewer_reputation(NEW.reviewer_id);
                        PERFORM refresh_reviewer_reputation(OLD.reviewer_id);
                    END IF;

                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER review_assignments_refresh_reputation_ins_del
                AFTER INSERT OR DELETE ON review_assignments
                FOR EACH ROW
                EXECUTE FUNCTION refresh_reviewer_reputation_from_source();

                CREATE TRIGGER review_assignments_refresh_reputation_update
                AFTER UPDATE OF status, reviewer_id ON review_assignments
                FOR EACH ROW
                WHEN (OLD.status IS DISTINCT FROM NEW.status
                      OR OLD.reviewer_id IS DISTINCT FROM NEW.reviewer_id)
                EXECUTE FUNCTION refresh_reviewer_reputation_from_source();

                CREATE TRIGGER feedback_ratings_refresh_reputation_ins_del
                AFTER INSERT OR DELETE ON feedback_ratings
                FOR EACH ROW
                EXECUTE FUNCTION refresh_reviewer_reputation_from_source();

                CREATE TRIGGER feedback_ratings_refresh_reputation_update
                AFTER UPDATE OF score, reviewer_id ON feedback_ratings
                FOR EACH ROW
                WHEN (OLD.score IS DISTINCT FROM NEW.score
                      OR OLD.reviewer_id IS DISTINCT FROM NEW.reviewer_id)
                EXECUTE FUNCTION refresh_reviewer_reputation_from_source();

                REVOKE ALL ON FUNCTION refresh_reviewer_reputation(bigint) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION refresh_reviewer_reputation(bigint) TO indiefi_app;
                REVOKE ALL ON FUNCTION refresh_reviewer_reputation_from_source() FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION refresh_reviewer_reputation_from_source() TO indiefi_app;

                DO $backfill$
                DECLARE
                    candidate_reviewer_id bigint;
                BEGIN
                    FOR candidate_reviewer_id IN
                        SELECT reviewer_id
                          FROM (
                                SELECT reviewer_id FROM review_assignments
                                UNION
                                SELECT reviewer_id FROM feedback_ratings
                                UNION
                                SELECT reviewer_id FROM reviewer_reputations
                               ) AS candidates
                         ORDER BY reviewer_id
                    LOOP
                        PERFORM refresh_reviewer_reputation(candidate_reviewer_id);
                    END LOOP;
                END;
                $backfill$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER feedback_ratings_refresh_reputation_update
                    ON feedback_ratings;
                DROP TRIGGER feedback_ratings_refresh_reputation_ins_del
                    ON feedback_ratings;
                DROP TRIGGER review_assignments_refresh_reputation_update
                    ON review_assignments;
                DROP TRIGGER review_assignments_refresh_reputation_ins_del
                    ON review_assignments;
                DROP FUNCTION refresh_reviewer_reputation_from_source();
                DROP FUNCTION refresh_reviewer_reputation(bigint);
                """);
        }
    }
}
