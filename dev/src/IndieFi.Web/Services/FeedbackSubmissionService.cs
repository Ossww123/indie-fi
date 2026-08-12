using IndieFi.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace IndieFi.Web.Services;

public enum FeedbackSubmissionOutcome
{
    Submitted,
    LeaseExpired,
    NotSubmittable,
}

public record FeedbackSubmissionResult(FeedbackSubmissionOutcome Outcome);

public class FeedbackSubmissionService
{
    // Completing the assignment, storing the feedback, earning credit, and completing the request
    // share one statement so no other transaction can interleave and no explicit transaction is needed.
    // PostgreSQL executes a data-changing CTE exactly once even when no later query references it,
    // so earned and finished are intentionally unreferenced. EF Core requires the Value alias for a scalar.
    // The ledger trigger maintains users.credit_balance, which the application cannot update.
    private const string SubmitSql = """
        WITH completed AS (
            UPDATE review_assignments
               SET status = 'Completed'
             WHERE id = @assignmentId
               AND reviewer_id = @reviewerId
               AND status = 'Active'
               AND leased_until > now()
            RETURNING id, request_id
        ),
        submitted AS (
            INSERT INTO feedback_submissions (assignment_id, body)
            SELECT completed.id, @body
              FROM completed
            RETURNING assignment_id
        ),
        earned AS (
            INSERT INTO credit_ledger_entries (user_id, kind, delta)
            SELECT @reviewerId, 'Earn', 1
              FROM completed
            RETURNING id
        ),
        finished AS (
            UPDATE review_requests
               SET status = 'Completed'
              FROM completed
             WHERE review_requests.id = completed.request_id
            RETURNING review_requests.id
        )
        SELECT assignment_id AS "Value" FROM submitted
        """;

    private const string LeaseExpiredSql = """
        SELECT status = 'Expired'
               OR (status = 'Active' AND leased_until <= now()) AS "Value"
          FROM review_assignments
         WHERE id = @assignmentId
           AND reviewer_id = @reviewerId
        """;

    private readonly AppDbContext _db;
    private readonly ILogger<FeedbackSubmissionService> _logger;

    public FeedbackSubmissionService(
        AppDbContext db,
        ILogger<FeedbackSubmissionService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<FeedbackSubmissionResult> SubmitAsync(
        long assignmentId,
        long reviewerId,
        string body)
    {
        var submittedAssignmentIds = await _db.Database
            .SqlQueryRaw<long>(
                SubmitSql,
                new NpgsqlParameter("assignmentId", assignmentId),
                new NpgsqlParameter("reviewerId", reviewerId),
                new NpgsqlParameter("body", body))
            .ToListAsync();

        if (submittedAssignmentIds.Count != 0)
        {
            return Result(FeedbackSubmissionOutcome.Submitted, assignmentId, reviewerId);
        }

        // Only failures pay for this second round trip. Looking up only the reviewer's own
        // assignment prevents callers from distinguishing another reviewer's assignment from a
        // missing one; lease expiry is exposed because obtaining another assignment is actionable.
        var leaseExpired = await _db.Database
            .SqlQueryRaw<bool>(
                LeaseExpiredSql,
                new NpgsqlParameter("assignmentId", assignmentId),
                new NpgsqlParameter("reviewerId", reviewerId))
            .ToListAsync();

        if (leaseExpired.Count != 0 && leaseExpired[0])
        {
            return Result(FeedbackSubmissionOutcome.LeaseExpired, assignmentId, reviewerId);
        }

        return Result(FeedbackSubmissionOutcome.NotSubmittable, assignmentId, reviewerId);
    }

    private FeedbackSubmissionResult Result(
        FeedbackSubmissionOutcome outcome,
        long assignmentId,
        long reviewerId)
    {
        _logger.LogInformation(
            "FeedbackSubmission outcome={Outcome} assignment={AssignmentId} reviewer={ReviewerId}",
            outcome, assignmentId, reviewerId);
        return new FeedbackSubmissionResult(outcome);
    }
}
