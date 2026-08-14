using IndieFi.Web.Data;
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
}

public record ReviewClaimResult(ReviewClaimOutcome Outcome, long? RequestId);

public class ReviewClaimService
{
    /// <summary>How long a reviewer holds a claimed request before it returns to the queue.</summary>
    private const int LeaseMinutes = 30;
    private const string UniqueViolation = "23505";
    private const string ReviewerGameIndex = "IX_review_assignments_reviewer_id_game_id";

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
        WITH picked AS MATERIALIZED (
            SELECT r.id, r.game_id
              FROM review_requests r
             WHERE r.status = 'Queued'
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
                status,
                leased_until)
            SELECT assigned.id, assigned.game_id, @reviewerId, 'Active',
                   now() + (@leaseMinutes * INTERVAL '1 minute')
              FROM assigned
            RETURNING request_id
        ),
        outcome AS (
            SELECT request_id FROM inserted
            UNION ALL
            SELECT CASE
                       WHEN EXISTS (SELECT 1 FROM active_user) THEN -1
                       ELSE -2
                   END
             WHERE NOT EXISTS (SELECT 1 FROM inserted)
        )
        SELECT request_id AS "Value" FROM outcome
        """;

    private readonly AppDbContext _db;
    private readonly ILogger<ReviewClaimService> _logger;

    public ReviewClaimService(AppDbContext db, ILogger<ReviewClaimService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<ReviewClaimResult> ClaimNextAsync(long reviewerId)
    {
        List<long> claimMarkers;

        try
        {
            claimMarkers = await _db.Database
                .SqlQueryRaw<long>(
                    ClaimSql,
                    new NpgsqlParameter("reviewerId", reviewerId),
                    new NpgsqlParameter("leaseMinutes", LeaseMinutes))
                .ToListAsync();
        }
        // The prefilter sees only its own snapshot, so two claims from one reviewer can both
        // pass and lock different request rows that SKIP LOCKED cannot coordinate; only that
        // reviewer-game backstop is an expected race here.
        catch (PostgresException exception)
            when (exception.SqlState == UniqueViolation
                && exception.ConstraintName == ReviewerGameIndex)
        {
            return Result(ReviewClaimOutcome.AlreadyHasThatGame, reviewerId, null);
        }

        var claimMarker = claimMarkers.Single();
        if (claimMarker == -2)
        {
            return Result(
                ReviewClaimOutcome.AccountUnavailable,
                reviewerId,
                null);
        }

        if (claimMarker == -1)
        {
            return Result(ReviewClaimOutcome.NoneAvailable, reviewerId, null);
        }

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
