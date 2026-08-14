using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Credits.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace IndieFi.Web.Modules.Feedback.Application;

public enum ReviewRequestOutcome
{
    Queued,
    InsufficientCredit,
    GameNotFound,
    NotOwner,
    Cancelled,
    NotCancellable,
    AccountUnavailable,
}

public record ReviewRequestResult(ReviewRequestOutcome Outcome);

public class ReviewRequestService
{
    /// <summary>PostgreSQL SQLSTATE for a check constraint violation.</summary>
    private const string CheckViolation = "23514";

    private const int RequestCost = 1;

    // FOR UPDATE is the cancellation linearization point: a competing claim must finish before
    // the status predicate is rechecked. The refund depends on the locked row, and the final
    // update depends on that refund, so every rejected cancellation leaves both tables untouched.
    // The request is updated only once in this statement because data-changing CTEs share the
    // statement's original snapshot. The ledger trigger maintains users.credit_balance.
    private const string CancelSql = """
        WITH locked AS (
            SELECT id, requester_id
              FROM review_requests
             WHERE id = @requestId
               AND requester_id = @userId
               AND status = 'Queued'
             FOR UPDATE
        ),
        refunded AS (
            INSERT INTO credit_ledger_entries (user_id, kind, delta)
            SELECT requester_id, 'Refund', 1
              FROM locked
            RETURNING id
        )
        UPDATE review_requests
           SET status = 'Cancelled', refund_entry_id = refunded.id
          FROM refunded
         WHERE review_requests.id = @requestId
        RETURNING review_requests.id AS "Value"
        """;

    private readonly AppDbContext _db;
    private readonly ILogger<ReviewRequestService> _logger;

    public ReviewRequestService(AppDbContext db, ILogger<ReviewRequestService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<ReviewRequestResult> RequestAsync(long gameId, long requesterId)
    {
        await using var transaction =
            await AccountMutationGuard.BeginTransactionIfNeededAsync(_db);
        if (!await AccountMutationGuard.LockActiveAsync(_db, requesterId))
        {
            await AccountMutationGuard.CommitIfOwnedAsync(transaction);
            return Result(
                ReviewRequestOutcome.AccountUnavailable,
                gameId,
                requesterId);
        }

        var game = await _db.Games
            .Where(game => game.Id == gameId)
            .Select(game => new { game.OwnerId })
            .SingleOrDefaultAsync();

        if (game is null)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(transaction);
            return Result(ReviewRequestOutcome.GameNotFound, gameId, requesterId);
        }

        if (game.OwnerId != requesterId)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(transaction);
            return Result(ReviewRequestOutcome.NotOwner, gameId, requesterId);
        }

        var spendEntry = new CreditLedgerEntry
        {
            UserId = requesterId,
            Kind = CreditEntryKind.Spend,
            Delta = -RequestCost,
        };

        var reviewRequest = new ReviewRequest
        {
            GameId = gameId,
            RequesterId = requesterId,
            SpendEntry = spendEntry,
        };

        _db.CreditLedgerEntries.Add(spendEntry);
        _db.ReviewRequests.Add(reviewRequest);

        // The balance check now lives inside the database CHECK constraint. Concurrent
        // trigger updates serialize on the same user row, so only one competing spend can
        // succeed. Insufficient credit raises instead of silently affecting no rows, and
        // the SaveChanges transaction rolls back all of its inserts.
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: CheckViolation })
        {
            await AccountMutationGuard.RollbackIfOwnedAsync(transaction);
            return Result(ReviewRequestOutcome.InsufficientCredit, gameId, requesterId);
        }

        await AccountMutationGuard.CommitIfOwnedAsync(transaction);
        return Result(ReviewRequestOutcome.Queued, gameId, requesterId);
    }

    public async Task<ReviewRequestResult> CancelAsync(long requestId, long userId)
    {
        await using var transaction =
            await AccountMutationGuard.BeginTransactionIfNeededAsync(_db);
        if (!await AccountMutationGuard.LockActiveAsync(_db, userId))
        {
            await AccountMutationGuard.CommitIfOwnedAsync(transaction);
            return new ReviewRequestResult(ReviewRequestOutcome.AccountUnavailable);
        }

        var cancelledRequestIds = await _db.Database
            .SqlQueryRaw<long>(
                CancelSql,
                new NpgsqlParameter("requestId", requestId),
                new NpgsqlParameter("userId", userId))
            .ToListAsync();
        await AccountMutationGuard.CommitIfOwnedAsync(transaction);

        var outcome = cancelledRequestIds.Count == 0
            ? ReviewRequestOutcome.NotCancellable
            : ReviewRequestOutcome.Cancelled;

        _logger.LogInformation(
            "ReviewRequest cancellation outcome={Outcome} request={RequestId} user={UserId}",
            outcome, requestId, userId);
        return new ReviewRequestResult(outcome);
    }

    private ReviewRequestResult Result(
        ReviewRequestOutcome outcome,
        long gameId,
        long requesterId)
    {
        _logger.LogInformation(
            "ReviewRequest outcome={Outcome} game={GameId} user={UserId}",
            outcome, gameId, requesterId);
        return new ReviewRequestResult(outcome);
    }
}
