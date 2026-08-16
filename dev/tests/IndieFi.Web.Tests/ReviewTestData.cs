using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Credits.Domain;
using IndieFi.Web.Modules.Feedback.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IndieFi.Web.Tests;

internal static class ReviewTestData
{
    public static async Task<User> CreateUserWithCreditAsync(
        AppDbContext db,
        int balance,
        string? email = null)
    {
        var user = TestAccountProtection.AddUser(
            db,
            email ?? $"{Guid.NewGuid():N}@example.test");

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
            CreationOperationId = Guid.NewGuid(),
            SpendEntry = spendEntry,
        };

        db.CreditLedgerEntries.Add(spendEntry);
        db.ReviewRequests.Add(request);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return request;
    }

    public static async Task<LiveAssignment> CreateLiveAssignmentAsync(
        PostgreSqlFixture fixture)
    {
        long reviewerId;
        long requestId;

        await using (var arrangeDb = fixture.NewDbContext())
        {
            // Claiming reads the whole queue, not one game, so leftovers from other tests would
            // be handed out instead of the rows this test arranged. Assignments disappear with
            // their requests through the cascade.
            await arrangeDb.ReviewRequests.ExecuteDeleteAsync(TestContext.Current.CancellationToken);

            var requester = await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, balance: 1);
            reviewerId = (await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0)).Id;
            var game = await ReviewTestData.CreateGameAsync(arrangeDb, requester.Id);
            requestId = (await ReviewTestData.CreateQueuedRequestAsync(
                arrangeDb,
                requester.Id,
                game.Id)).Id;
        }

        await using var claimDb = fixture.NewDbContext();
        var claimResult = await new ReviewClaimService(
            claimDb,
            NullLogger<ReviewClaimService>.Instance,
            new TestNotificationEventPublisher()).ClaimNextAsync(reviewerId);
        var assignmentId = await claimDb.ReviewAssignments
            .Where(item => item.RequestId == requestId)
            .Select(item => item.Id)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ReviewClaimOutcome.Claimed, claimResult.Outcome);
        Assert.Equal(requestId, claimResult.RequestId);
        return new LiveAssignment(assignmentId, requestId, reviewerId);
    }

    public sealed record LiveAssignment(long AssignmentId, long RequestId, long ReviewerId);
}
