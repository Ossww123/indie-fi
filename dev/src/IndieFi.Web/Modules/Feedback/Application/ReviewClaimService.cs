using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Contracts;
using IndieFi.Web.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace IndieFi.Web.Modules.Feedback.Application;

public enum ReviewClaimOutcome
{
    Claimed,
    NoneAvailable,
    AlreadyHasThatGame,
    AccountUnavailable,
    InvalidOperationId,
}

public record ReviewClaimResult(ReviewClaimOutcome Outcome, long? RequestId);

public class ReviewClaimService
{
    /// <summary>How long a reviewer holds a claimed request before it returns to the queue.</summary>
    private const int LeaseMinutes = 30;
    private const string UniqueViolation = "23505";
    private const string ReviewerGameIndex = "IX_review_assignments_reviewer_id_game_id";
    private const string ReviewerOperationIndex =
        "IX_review_assignments_reviewer_id_claim_operation_id";

    // SKIP LOCKED lets concurrent reviewers bypass rows another claim is already locking,
    // avoiding waits between claims. Ordering by ID keeps the oldest request first, gives
    // equal timestamps a stable order, and preserves a request's place if it returns to the queue.
    // NOT EXISTS skips games the reviewer already holds before a request is picked, so the normal
    // claim path moves cleanly to the next eligible request instead of hitting the unique backstop.
    // The added subquery makes the lock target less obvious, so OF r explicitly limits the lock
    // to the candidate request row that SKIP LOCKED coordinates between concurrent claims.
    // The request is selected before the account row is locked. This preserves SKIP LOCKED
    // concurrency between claims while the active-user CTE still serializes the final mutation
    // against suspension. A suspension that wins makes active_user empty; a claim that wins is
    // visible when suspension subsequently collects that user's assignments.
    private const string ClaimSql = """
        WITH existing AS MATERIALIZED (
            SELECT request_id
              FROM review_assignments
             WHERE reviewer_id = @reviewerId
               AND claim_operation_id = @operationId
        ),
        picked AS MATERIALIZED (
            SELECT r.id, r.game_id
              FROM review_requests r
             WHERE r.status = 'Queued'
               AND NOT EXISTS (SELECT 1 FROM existing)
               AND r.requester_id <> @reviewerId
               AND NOT EXISTS (
                       SELECT 1
                         FROM review_assignments a
                        WHERE a.reviewer_id = @reviewerId
                          AND a.game_id = r.game_id
                          AND a.status <> 'Expired'
                   )
             ORDER BY r.id
             LIMIT 1
             FOR UPDATE OF r SKIP LOCKED
        ),
        active_user AS MATERIALIZED (
            SELECT u.id
              FROM users u
              CROSS JOIN (
                  SELECT count(*) AS picked_count
                    FROM picked
              ) AS dependency
             WHERE u.id = @reviewerId
               AND u.account_status = 'Active'
               AND u.suspended_until IS NULL
             FOR UPDATE OF u
        ),
        assigned AS (
            UPDATE review_requests
               SET status = 'Assigned'
              FROM picked, active_user
             WHERE review_requests.id = picked.id
            RETURNING review_requests.id, review_requests.game_id
        ),
        inserted AS (
            INSERT INTO review_assignments (
                request_id,
                game_id,
                reviewer_id,
                claim_operation_id,
                status,
                leased_until)
            SELECT assigned.id, assigned.game_id, @reviewerId, @operationId, 'Active',
                   now() + (@leaseMinutes * INTERVAL '1 minute')
              FROM assigned
            RETURNING request_id
        ),
        outcome AS (
            SELECT request_id FROM existing
            UNION ALL
            SELECT request_id FROM inserted
            UNION ALL
            SELECT CASE
                       WHEN EXISTS (SELECT 1 FROM active_user) THEN -1
                       ELSE -2
                   END
             WHERE NOT EXISTS (SELECT 1 FROM existing)
               AND NOT EXISTS (SELECT 1 FROM inserted)
        )
        SELECT request_id AS "Value" FROM outcome
        """;

    private readonly AppDbContext _db;
    private readonly ILogger<ReviewClaimService> _logger;
    private readonly INotificationEventPublisher _notificationPublisher;

    public ReviewClaimService(
        AppDbContext db,
        ILogger<ReviewClaimService> logger,
        INotificationEventPublisher notificationPublisher)
    {
        _db = db;
        _logger = logger;
        _notificationPublisher = notificationPublisher;
    }

    public Task<ReviewClaimResult> ClaimNextAsync(long reviewerId)
    {
        return ClaimNextAsync(reviewerId, Guid.NewGuid());
    }

    public async Task<ReviewClaimResult> ClaimNextAsync(
        long reviewerId,
        Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            return Result(ReviewClaimOutcome.InvalidOperationId, reviewerId, null);
        }

        await using var transaction =
            await AccountMutationGuard.BeginTransactionIfNeededAsync(_db);
        var activeTransaction = _db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Review claim requires a transaction.");
        const string savepointName = "review_claim_attempt";
        await activeTransaction.CreateSavepointAsync(savepointName);
        long claimMarker;

        try
        {
            var claimMarkers = await _db.Database
                .SqlQueryRaw<long>(
                    ClaimSql,
                    new NpgsqlParameter("reviewerId", reviewerId),
                    new NpgsqlParameter("operationId", operationId),
                    new NpgsqlParameter("leaseMinutes", LeaseMinutes))
                .ToListAsync();
            claimMarker = claimMarkers.Single();
            await activeTransaction.ReleaseSavepointAsync(savepointName);
        }
        // The prefilter sees only its own snapshot, so two claims from one reviewer can both
        // pass and lock different request rows that SKIP LOCKED cannot coordinate; only that
        // reviewer-game backstop is an expected race here.
        catch (PostgresException exception)
            when (exception.SqlState == UniqueViolation
                && exception.ConstraintName == ReviewerOperationIndex)
        {
            await activeTransaction.RollbackToSavepointAsync(savepointName);
            await activeTransaction.ReleaseSavepointAsync(savepointName);
            claimMarker = await _db.ReviewAssignments
                .Where(assignment => assignment.ReviewerId == reviewerId)
                .Where(assignment => assignment.ClaimOperationId == operationId)
                .Select(assignment => assignment.RequestId)
                .SingleAsync();
        }
        catch (PostgresException exception)
            when (exception.SqlState == UniqueViolation
                && exception.ConstraintName == ReviewerGameIndex)
        {
            await activeTransaction.RollbackToSavepointAsync(savepointName);
            await activeTransaction.ReleaseSavepointAsync(savepointName);
            await AccountMutationGuard.CommitIfOwnedAsync(transaction);
            return Result(ReviewClaimOutcome.AlreadyHasThatGame, reviewerId, null);
        }

        if (claimMarker == -2)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(transaction);
            return Result(
                ReviewClaimOutcome.AccountUnavailable,
                reviewerId,
                null);
        }

        if (claimMarker == -1)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(transaction);
            return Result(ReviewClaimOutcome.NoneAvailable, reviewerId, null);
        }

        var assignment = await _db.ReviewAssignments
            .AsNoTracking()
            .Where(item => item.ReviewerId == reviewerId
                && item.ClaimOperationId == operationId)
            .Select(item => new { item.Id, item.CreatedAt })
            .SingleAsync();
        await _notificationPublisher.StageAsync(
            NotificationEventId.Create(
                NotificationKind.ReviewAssigned,
                assignment.Id,
                reviewerId),
            reviewerId,
            NotificationKind.ReviewAssigned,
            NotificationSourceTypes.ReviewAssignment,
            assignment.Id,
            assignment.CreatedAt);
        await _db.SaveChangesAsync();
        await AccountMutationGuard.CommitIfOwnedAsync(transaction);

        return Result(ReviewClaimOutcome.Claimed, reviewerId, claimMarker);
    }

    private ReviewClaimResult Result(
        ReviewClaimOutcome outcome,
        long reviewerId,
        long? requestId)
    {
        _logger.LogInformation(
            "ReviewClaim outcome={Outcome} reviewer={ReviewerId} request={RequestId}",
            outcome, reviewerId, requestId);
        return new ReviewClaimResult(outcome, requestId);
    }
}
