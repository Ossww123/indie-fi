using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using Xunit;

namespace IndieFi.Web.Tests;

internal static class ReviewTestData
{
    public static async Task<User> CreateUserWithCreditAsync(AppDbContext db, int balance)
    {
        var user = new User
        {
            Email = $"{Guid.NewGuid():N}@example.test",
            PasswordHash = "test-password-hash",
        };

        db.Users.Add(user);

        if (balance > 0)
        {
            db.CreditLedgerEntries.Add(new CreditLedgerEntry
            {
                User = user,
                Kind = CreditEntryKind.Grant,
                Delta = balance,
            });
        }

        // Credit balance is derived by a database trigger, so ledger-only setup preserves the
        // invariant for tests that reconcile every user's cached balance with ledger history.
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user;
    }

    public static async Task<Game> CreateGameAsync(AppDbContext db, long ownerId)
    {
        var game = new Game
        {
            OwnerId = ownerId,
            Title = $"Test Game {Guid.NewGuid():N}",
        };

        db.Games.Add(game);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return game;
    }

    public static async Task<ReviewRequest> CreateQueuedRequestAsync(
        AppDbContext db,
        long requesterId,
        long gameId)
    {
        var spendEntry = new CreditLedgerEntry
        {
            UserId = requesterId,
            Kind = CreditEntryKind.Spend,
            Delta = -1,
        };
        var request = new ReviewRequest
        {
            GameId = gameId,
            RequesterId = requesterId,
            SpendEntry = spendEntry,
        };

        db.CreditLedgerEntries.Add(spendEntry);
        db.ReviewRequests.Add(request);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return request;
    }
}
