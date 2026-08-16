using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Contracts;
using IndieFi.Web.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IndieFi.Web.Modules.Feedback.Application;

public sealed record ReviewDeadlineNotificationResult(int StagedCount);

public sealed class ReviewDeadlineNotificationService
{
    public const int DefaultBatchSize = 100;

    private const string ClaimDueSql = """
        WITH due AS (
            SELECT id
              FROM review_assignments
             WHERE status = 'Active'
               AND deadline_notification_staged_at IS NULL
               AND leased_until > now()
               AND leased_until <= now() + (@leadMinutes * INTERVAL '1 minute')
             ORDER BY leased_until, id
             LIMIT @batchSize
             FOR UPDATE SKIP LOCKED
        )
        UPDATE review_assignments AS assignment
           SET deadline_notification_staged_at = now()
          FROM due
         WHERE assignment.id = due.id
        RETURNING assignment.id AS "AssignmentId",
                  assignment.reviewer_id AS "ReviewerId",
                  assignment.leased_until AS "LeasedUntil"
        """;

    private readonly AppDbContext _db;
    private readonly INotificationEventPublisher _notificationPublisher;

    public ReviewDeadlineNotificationService(
        AppDbContext db,
        INotificationEventPublisher notificationPublisher)
    {
        _db = db;
        _notificationPublisher = notificationPublisher;
    }

    public async Task<ReviewDeadlineNotificationResult> StageDueAsync(
        int batchSize = DefaultBatchSize,
        CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        await using var transaction =
            await AccountMutationGuard.BeginTransactionIfNeededAsync(
                _db,
                cancellationToken);
        var due = await _db.Database
            .SqlQueryRaw<ReviewDeadlineCandidate>(
                ClaimDueSql,
                new NpgsqlParameter(
                    "leadMinutes",
                    checked((int)NotificationPolicy.ReviewDeadlineLeadTime.TotalMinutes)),
                new NpgsqlParameter("batchSize", batchSize))
            .ToListAsync(cancellationToken);

        foreach (var candidate in due)
        {
            await _notificationPublisher.StageAsync(
                NotificationEventId.Create(
                    NotificationKind.ReviewDeadlineSoon,
                    candidate.AssignmentId,
                    candidate.ReviewerId),
                candidate.ReviewerId,
                NotificationKind.ReviewDeadlineSoon,
                NotificationSourceTypes.ReviewAssignment,
                candidate.AssignmentId,
                candidate.LeasedUntil - NotificationPolicy.ReviewDeadlineLeadTime,
                cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
        await AccountMutationGuard.CommitIfOwnedAsync(transaction, cancellationToken);
        return new ReviewDeadlineNotificationResult(due.Count);
    }

    private sealed record ReviewDeadlineCandidate(
        long AssignmentId,
        long ReviewerId,
        DateTimeOffset LeasedUntil);
}
