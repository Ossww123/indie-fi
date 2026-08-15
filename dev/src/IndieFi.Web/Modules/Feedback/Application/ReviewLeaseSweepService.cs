using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Application;
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
            RETURNING request_id
        ),
        requeued AS (
            UPDATE review_requests
               SET status = 'Queued'
              FROM expired
             WHERE review_requests.id = expired.request_id
               AND review_requests.status = 'Assigned'
            RETURNING review_requests.id
        )
        SELECT count(*) AS "Value" FROM expired
        """;

    private readonly AppDbContext _db;
    private readonly ILogger<ReviewLeaseSweepService> _logger;

    public ReviewLeaseSweepService(
        AppDbContext db,
        ILogger<ReviewLeaseSweepService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<ReviewLeaseSweepResult> SweepAsync(long? requestedByUserId = null)
    {
        await using var transaction = requestedByUserId is null
            ? null
            : await AccountMutationGuard.BeginTransactionIfNeededAsync(_db);
        if (requestedByUserId is long userId
            && !await AccountMutationGuard.LockActiveAsync(_db, userId))
        {
            await AccountMutationGuard.CommitIfOwnedAsync(transaction);
            return new ReviewLeaseSweepResult(
                0,
                ReviewLeaseSweepOutcome.AccountUnavailable);
        }

        var expiredCounts = await _db.Database
            .SqlQueryRaw<long>(SweepSql)
            .ToListAsync();
        await AccountMutationGuard.CommitIfOwnedAsync(transaction);

        return Result(checked((int)expiredCounts[0]));
    }

    private ReviewLeaseSweepResult Result(int expiredCount)
    {
        _logger.LogInformation(
            "ReviewLeaseSweep expired={ExpiredCount}",
            expiredCount);
        return new ReviewLeaseSweepResult(expiredCount);
    }
}
