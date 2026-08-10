using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace IndieFi.Web.Services;

public enum ReviewRequestOutcome
{
    Queued,
    InsufficientCredit,
    GameNotFound,
    NotOwner,
}

public record ReviewRequestResult(ReviewRequestOutcome Outcome);

public class ReviewRequestService
{
    /// <summary>PostgreSQL SQLSTATE for a check constraint violation.</summary>
    private const string CheckViolation = "23514";

    private const int RequestCost = 1;

    private readonly AppDbContext _db;
    private readonly ILogger<ReviewRequestService> _logger;

    public ReviewRequestService(AppDbContext db, ILogger<ReviewRequestService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<ReviewRequestResult> RequestAsync(long gameId, long requesterId)
    {
        var game = await _db.Games
            .Where(game => game.Id == gameId)
            .Select(game => new { game.OwnerId })
            .SingleOrDefaultAsync();

        if (game is null)
        {
            return Result(ReviewRequestOutcome.GameNotFound, gameId, requesterId);
        }

        if (game.OwnerId != requesterId)
        {
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
            return Result(ReviewRequestOutcome.InsufficientCredit, gameId, requesterId);
        }

        return Result(ReviewRequestOutcome.Queued, gameId, requesterId);
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
