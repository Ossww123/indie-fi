using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Contracts;
using IndieFi.Web.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IndieFi.Web.Modules.Feedback.Application;

public enum ReviewLeaseSweepOutcome
{
    Swept,
    AccountUnavailable,
}

public record ReviewLeaseSweepResult(
    int ExpiredCount,
    ReviewLeaseSweepOutcome Outcome = ReviewLeaseSweepOutcome.Swept);

public class ReviewLeaseSweepService
{
    // Assignments are updated before requests to match submission's lock order, avoiding
    // opposite-order deadlocks when sweep races with submission. Requiring 'Active' is what
    // makes completing and expiring an assignment produce a single winner: submission requires
    // the complementary leased_until > now(), so whichever transaction commits first leaves the
    // other with no rows. Requeueing only an assigned request keeps a request that reached some
    // other state from being pulled back into the queue; no application path reaches that state
    // today, so the test that covers it forces the row by hand. PostgreSQL executes a
    // data-changing CTE even when no later query references it, so requeued is unreferenced.
    // EF Core requires the Value alias when materializing a scalar result.
    private const string SweepSql = """
        WITH expired AS (
            UPDATE review_assignments
               SET status = 'Expired', expired_at = now()
             WHERE status = 'Active'
               AND leased_until <= now()
            RETURNING id, request_id, reviewer_id, expired_at
        ),
        requeued AS (
            UPDATE review_requests
               SET status = 'Queued'
              FROM expired
             WHERE review_requests.id = expired.request_id
               AND review_requests.status = 'Assigned'
            RETURNING review_requests.id
        )
        SELECT id AS "AssignmentId",
               reviewer_id AS "ReviewerId",
               expired_at AS "ExpiredAt"
          FROM expired
         ORDER BY id
        """;

    private readonly AppDbContext _db;
    private readonly ILogger<ReviewLeaseSweepService> _logger;
    private readonly INotificationEventPublisher _notificationPublisher;

    public ReviewLeaseSweepService(
        AppDbContext db,
        ILogger<ReviewLeaseSweepService> logger,
        INotificationEventPublisher notificationPublisher)
    {
        _db = db;
        _logger = logger;
        _notificationPublisher = notificationPublisher;
    }

    public async Task<ReviewLeaseSweepResult> SweepAsync(long? requestedByUserId = null)
    {
        await using var transaction =
            await AccountMutationGuard.BeginTransactionIfNeededAsync(_db);
        if (requestedByUserId is long userId
            && !await AccountMutationGuard.LockActiveAsync(_db, userId))
        {
            await AccountMutationGuard.CommitIfOwnedAsync(transaction);
            return new ReviewLeaseSweepResult(
                0,
                ReviewLeaseSweepOutcome.AccountUnavailable);
        }

        var expired = await _db.Database
            .SqlQueryRaw<ExpiredReviewAssignment>(SweepSql)
            .ToListAsync();

        foreach (var assignment in expired)
        {
            await _notificationPublisher.StageAsync(
                NotificationEventId.Create(
                    NotificationKind.ReviewExpired,
                    assignment.AssignmentId,
                    assignment.ReviewerId),
                assignment.ReviewerId,
                NotificationKind.ReviewExpired,
                NotificationSourceTypes.ReviewAssignment,
                assignment.AssignmentId,
                assignment.ExpiredAt);
        }

        await _db.SaveChangesAsync();
        await AccountMutationGuard.CommitIfOwnedAsync(transaction);

        return Result(expired.Count);
    }

    private ReviewLeaseSweepResult Result(int expiredCount)
    {
        _logger.LogInformation(
            "ReviewLeaseSweep expired={ExpiredCount}",
            expiredCount);
        return new ReviewLeaseSweepResult(expiredCount);
    }

    private sealed record ExpiredReviewAssignment(
        long AssignmentId,
        long ReviewerId,
        DateTimeOffset ExpiredAt);
}
