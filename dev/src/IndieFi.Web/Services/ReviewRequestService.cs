using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

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

        // The successful path uses five database round trips: pre-check, transaction begin,
        // conditional debit, the batched SaveChanges, and commit.
        // ExecuteUpdateAsync runs immediately without the change tracker, while the inserts
        // wait for SaveChangesAsync. The explicit transaction prevents a debit from being
        // committed without its ledger entry and review request.
        await using var transaction = await _db.Database.BeginTransactionAsync();

        // Reading the balance before writing would let another request spend it in between.
        // Putting the balance check in the UPDATE makes the check and debit one atomic
        // operation, so only one concurrent request can affect the row. The non-negative
        // CHECK constraint remains the final defense.
        var affected = await _db.Users
            .Where(user => user.Id == requesterId && user.CreditBalance >= RequestCost)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                user => user.CreditBalance, user => user.CreditBalance - RequestCost));

        if (affected == 0)
        {
            await transaction.RollbackAsync();
            return Result(ReviewRequestOutcome.InsufficientCredit, gameId, requesterId);
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
        await _db.SaveChangesAsync();

        await transaction.CommitAsync();
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
