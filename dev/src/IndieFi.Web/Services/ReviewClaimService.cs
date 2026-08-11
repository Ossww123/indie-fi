using IndieFi.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace IndieFi.Web.Services;

public enum ReviewClaimOutcome
{
    Claimed,
    NoneAvailable,
}

public record ReviewClaimResult(ReviewClaimOutcome Outcome, long? RequestId);

public class ReviewClaimService
{
    /// <summary>How long a reviewer holds a claimed request before it returns to the queue.</summary>
    private const int LeaseMinutes = 30;

    // SKIP LOCKED lets concurrent reviewers bypass rows another claim is already locking,
    // avoiding waits between claims. Ordering by ID keeps the oldest request first, gives
    // equal timestamps a stable order, and preserves a request's place if it returns to the queue.
    // Selection, state transition, and assignment insertion share one statement so no other
    // transaction can interleave and no explicit transaction is needed. If selection is empty,
    // the update and insert also affect no rows, leaving no result row. EF Core requires the
    // Value alias when materializing a scalar result.
    private const string ClaimSql = """
        WITH picked AS (
            SELECT r.id
              FROM review_requests r
             WHERE r.status = 'Queued'
               AND r.requester_id <> @reviewerId
             ORDER BY r.id
             LIMIT 1
             FOR UPDATE SKIP LOCKED
        ),
        assigned AS (
            UPDATE review_requests
               SET status = 'Assigned'
              FROM picked
             WHERE review_requests.id = picked.id
            RETURNING review_requests.id
        )
        INSERT INTO review_assignments (request_id, reviewer_id, status, leased_until)
        SELECT assigned.id, @reviewerId, 'Active', now() + (@leaseMinutes * INTERVAL '1 minute')
          FROM assigned
        RETURNING request_id AS "Value"
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
        var claimedRequestIds = await _db.Database
            .SqlQueryRaw<long>(
                ClaimSql,
                new NpgsqlParameter("reviewerId", reviewerId),
                new NpgsqlParameter("leaseMinutes", LeaseMinutes))
            .ToListAsync();

        if (claimedRequestIds.Count == 0)
        {
            return Result(ReviewClaimOutcome.NoneAvailable, reviewerId, null);
        }

        return Result(ReviewClaimOutcome.Claimed, reviewerId, claimedRequestIds[0]);
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
