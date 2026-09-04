using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Contracts;
using IndieFi.Web.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace IndieFi.Web.Modules.Feedback.Application;

public enum FeedbackSubmissionOutcome
{
    Submitted,
    LeaseExpired,
    NotSubmittable,
    AccountUnavailable,
}

public record FeedbackSubmissionResult(FeedbackSubmissionOutcome Outcome);

public class FeedbackSubmissionService
{
    // Completing the assignment, storing the feedback, earning credit, and completing the request
    // share one statement so no other transaction can interleave and no explicit transaction is needed.
    // PostgreSQL executes a data-changing CTE exactly once even when no later query references it,
    // so finished is intentionally unreferenced. EF Core requires the Value alias for a scalar.
    // The ledger trigger maintains users.credit_balance, which the application cannot update.
    private const string SubmitSql = """
        WITH completed AS (
            UPDATE review_assignments
               SET status = 'Completed', completed_at = now()
             WHERE id = @assignmentId
               AND reviewer_id = @reviewerId
               AND status = 'Active'
               AND leased_until > now()
            RETURNING id, request_id, reviewer_id
        ),
        earned AS (
            INSERT INTO credit_ledger_entries (user_id, kind, delta)
            SELECT reviewer_id, 'Earn', 1
              FROM completed
            RETURNING id, user_id
        ),
        submitted AS (
            INSERT INTO feedback_submissions (
                assignment_id, reviewer_id, body, earn_entry_id)
            SELECT completed.id, completed.reviewer_id, @body, earned.id
              FROM completed
              JOIN earned ON earned.user_id = completed.reviewer_id
            RETURNING assignment_id
        ),
        finished AS (
            UPDATE review_requests
               SET status = 'Completed', completed_at = now()
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

    private const string AlreadySubmittedSql = """
        SELECT EXISTS (
            SELECT 1
              FROM feedback_submissions AS submission
              JOIN review_assignments AS assignment
                ON assignment.id = submission.assignment_id
               AND assignment.reviewer_id = submission.reviewer_id
             WHERE assignment.id = @assignmentId
               AND assignment.reviewer_id = @reviewerId
               AND assignment.status = 'Completed'
        ) AS "Value"
        """;

    private readonly AppDbContext _db;
    private readonly ILogger<FeedbackSubmissionService> _logger;
    private readonly INotificationEventPublisher _notificationPublisher;

    public FeedbackSubmissionService(
        AppDbContext db,
        ILogger<FeedbackSubmissionService> logger,
        INotificationEventPublisher notificationPublisher)
    {
        _db = db;
        _logger = logger;
        _notificationPublisher = notificationPublisher;
    }

    public async Task<FeedbackSubmissionResult> SubmitAsync(
        long assignmentId,
        long reviewerId,
        string body)
    {
        await using var transaction =
            await AccountMutationGuard.BeginTransactionIfNeededAsync(_db);
        if (!await AccountMutationGuard.LockActiveAsync(_db, reviewerId))
        {
            await AccountMutationGuard.CommitIfOwnedAsync(transaction);
            return Result(
                FeedbackSubmissionOutcome.AccountUnavailable,
                assignmentId,
                reviewerId);
        }

        var submittedAssignmentIds = await _db.Database
            .SqlQueryRaw<long>(
                SubmitSql,
                new NpgsqlParameter("assignmentId", assignmentId),
                new NpgsqlParameter("reviewerId", reviewerId),
                new NpgsqlParameter("body", body))
            .ToListAsync();

        if (submittedAssignmentIds.Count != 0)
        {
            await StageFeedbackCompletedAsync(assignmentId);
            await _db.SaveChangesAsync();
            await AccountMutationGuard.CommitIfOwnedAsync(transaction);
            return Result(FeedbackSubmissionOutcome.Submitted, assignmentId, reviewerId);
        }

        var alreadySubmitted = await _db.Database
            .SqlQueryRaw<bool>(
                AlreadySubmittedSql,
                new NpgsqlParameter("assignmentId", assignmentId),
                new NpgsqlParameter("reviewerId", reviewerId))
            .SingleAsync();
        if (alreadySubmitted)
        {
            await StageFeedbackCompletedAsync(assignmentId);
            await _db.SaveChangesAsync();
            await AccountMutationGuard.CommitIfOwnedAsync(transaction);
            return Result(FeedbackSubmissionOutcome.Submitted, assignmentId, reviewerId);
        }

        // Only failures pay for the state lookups. Looking up only the reviewer's own assignment
        // prevents callers from distinguishing another reviewer's assignment from a missing one;
        // lease expiry is exposed because obtaining another assignment is actionable.
        var leaseExpired = await _db.Database
            .SqlQueryRaw<bool>(
                LeaseExpiredSql,
                new NpgsqlParameter("assignmentId", assignmentId),
                new NpgsqlParameter("reviewerId", reviewerId))
            .ToListAsync();

        if (leaseExpired.Count != 0 && leaseExpired[0])
        {
            await AccountMutationGuard.CommitIfOwnedAsync(transaction);
            return Result(FeedbackSubmissionOutcome.LeaseExpired, assignmentId, reviewerId);
        }

        await AccountMutationGuard.CommitIfOwnedAsync(transaction);
        return Result(FeedbackSubmissionOutcome.NotSubmittable, assignmentId, reviewerId);
    }

    private async Task StageFeedbackCompletedAsync(long assignmentId)
    {
        var source = await (
                from submission in _db.FeedbackSubmissions.AsNoTracking()
                join assignment in _db.ReviewAssignments.AsNoTracking()
                    on submission.AssignmentId equals assignment.Id
                join request in _db.ReviewRequests.AsNoTracking()
                    on assignment.RequestId equals request.Id
                join game in _db.Games.AsNoTracking()
                    on request.GameId equals game.Id
                where submission.AssignmentId == assignmentId
                select new
                {
                    submission.Id,
                    RecipientUserId = game.OwnerId,
                    submission.CreatedAt,
                })
            .SingleAsync();
        await _notificationPublisher.StageAsync(
            NotificationEventId.Create(
                NotificationKind.FeedbackCompleted,
                source.Id,
                source.RecipientUserId),
            source.RecipientUserId,
            NotificationKind.FeedbackCompleted,
            NotificationSourceTypes.FeedbackSubmission,
            source.Id,
            source.CreatedAt);
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
