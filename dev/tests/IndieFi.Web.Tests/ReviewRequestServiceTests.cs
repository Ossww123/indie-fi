using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class ReviewRequestServiceTests
{
    private readonly PostgreSqlFixture _fixture;

    public ReviewRequestServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RequestAsync_WhenBalanceIsEnough_SpendsOneCreditAndQueuesTheRequest()
    {
        long userId;
        long gameId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var arrangedUser = await CreateUserWithCreditAsync(arrangeDb, balance: 3);
            userId = arrangedUser.Id;
            gameId = (await CreateGameAsync(arrangeDb, userId)).Id;
        }

        ReviewRequestResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await NewService(serviceDb).RequestAsync(gameId, userId);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var user = await verificationDb.Users.SingleAsync(
            user => user.Id == userId,
            TestContext.Current.CancellationToken);
        var spendEntries = await verificationDb.CreditLedgerEntries
            .Where(entry =>
                entry.UserId == userId &&
                entry.Kind == CreditEntryKind.Spend)
            .ToListAsync(TestContext.Current.CancellationToken);
        var requests = await verificationDb.ReviewRequests
            .Where(request => request.RequesterId == userId && request.GameId == gameId)
            .ToListAsync(TestContext.Current.CancellationToken);
        var ledgerSum = await LedgerSumAsync(verificationDb, userId);

        Assert.Equal(ReviewRequestOutcome.Queued, result.Outcome);
        Assert.Equal(2, user.CreditBalance);
        var spendEntry = Assert.Single(spendEntries);
        Assert.Equal(-1, spendEntry.Delta);
        var request = Assert.Single(requests);
        Assert.Equal(userId, request.RequesterId);
        Assert.Equal(gameId, request.GameId);
        Assert.Equal(spendEntry.Id, request.SpendEntryId);
        Assert.Equal(user.CreditBalance, ledgerSum);
    }

    [Fact]
    public async Task RequestAsync_WhenBalanceIsZero_WritesNothing()
    {
        long userId;
        long gameId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var user = await CreateUserWithCreditAsync(arrangeDb, balance: 0);
            userId = user.Id;
            gameId = (await CreateGameAsync(arrangeDb, userId)).Id;
        }

        ReviewRequestResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await NewService(serviceDb).RequestAsync(gameId, userId);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var balance = await verificationDb.Users
            .Where(user => user.Id == userId)
            .Select(user => user.CreditBalance)
            .SingleAsync(TestContext.Current.CancellationToken);
        var spendCount = await verificationDb.CreditLedgerEntries.CountAsync(
            entry => entry.UserId == userId && entry.Kind == CreditEntryKind.Spend,
            TestContext.Current.CancellationToken);
        var requestCount = await verificationDb.ReviewRequests.CountAsync(
            request => request.RequesterId == userId && request.GameId == gameId,
            TestContext.Current.CancellationToken);

        Assert.Equal(ReviewRequestOutcome.InsufficientCredit, result.Outcome);

        // The result alone cannot prove that the debit was rolled back. Leaving any
        // failure-path trace would make the cached balance diverge from the ledger.
        Assert.Equal(0, balance);
        Assert.Equal(0, spendCount);
        Assert.Equal(0, requestCount);
    }

    [Fact]
    public async Task RequestAsync_WhenGameBelongsToSomeoneElse_WritesNothing()
    {
        long requesterId;
        long gameId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            requesterId = (await CreateUserWithCreditAsync(arrangeDb, balance: 3)).Id;
            var owner = await CreateUserWithCreditAsync(arrangeDb, balance: 0);
            gameId = (await CreateGameAsync(arrangeDb, owner.Id)).Id;
        }

        ReviewRequestResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await NewService(serviceDb).RequestAsync(gameId, requesterId);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var balance = await verificationDb.Users
            .Where(user => user.Id == requesterId)
            .Select(user => user.CreditBalance)
            .SingleAsync(TestContext.Current.CancellationToken);
        var spendCount = await verificationDb.CreditLedgerEntries.CountAsync(
            entry => entry.UserId == requesterId && entry.Kind == CreditEntryKind.Spend,
            TestContext.Current.CancellationToken);
        var requestCount = await verificationDb.ReviewRequests.CountAsync(
            request => request.RequesterId == requesterId && request.GameId == gameId,
            TestContext.Current.CancellationToken);

        Assert.Equal(ReviewRequestOutcome.NotOwner, result.Outcome);
        Assert.Equal(3, balance);
        Assert.Equal(0, spendCount);
        Assert.Equal(0, requestCount);
    }

    [Fact]
    public async Task RequestAsync_WhenOneCreditIsRequestedManyTimesAtOnce_SpendsItExactlyOnce()
    {
        const int RequestCount = 8;
        long userId;
        long gameId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var user = await CreateUserWithCreditAsync(arrangeDb, balance: 1);
            userId = user.Id;
            gameId = (await CreateGameAsync(arrangeDb, userId)).Id;
        }

        // Fire all eight requests on separate connections. With one credit, a read-then-write
        // implementation would let callers pass the same stale check; putting the check in the
        // UPDATE WHERE makes check-and-debit atomic, so exactly one UPDATE can affect the row.
        var outcomes = await Task.WhenAll(Enumerable.Range(0, RequestCount).Select(async _ =>
        {
            await using var serviceDb = _fixture.NewDbContext();
            return (await NewService(serviceDb).RequestAsync(gameId, userId)).Outcome;
        }));

        await using var verificationDb = _fixture.NewDbContext();
        var balance = await verificationDb.Users
            .Where(user => user.Id == userId)
            .Select(user => user.CreditBalance)
            .SingleAsync(TestContext.Current.CancellationToken);
        var spendEntries = await verificationDb.CreditLedgerEntries
            .Where(entry =>
                entry.UserId == userId &&
                entry.Kind == CreditEntryKind.Spend)
            .ToListAsync(TestContext.Current.CancellationToken);
        var requests = await verificationDb.ReviewRequests
            .Where(request => request.RequesterId == userId && request.GameId == gameId)
            .ToListAsync(TestContext.Current.CancellationToken);
        var ledgerSum = await LedgerSumAsync(verificationDb, userId);

        Assert.Equal(1, outcomes.Count(outcome => outcome == ReviewRequestOutcome.Queued));
        Assert.Equal(
            RequestCount - 1,
            outcomes.Count(outcome => outcome == ReviewRequestOutcome.InsufficientCredit));
        Assert.Equal(0, balance);
        Assert.Single(spendEntries);
        Assert.Single(requests);
        Assert.Equal(balance, ledgerSum);
    }

    // The service only uses its logger for outcome records; these tests assert database state.
    private static ReviewRequestService NewService(AppDbContext db) =>
        new(db, NullLogger<ReviewRequestService>.Instance);

    private static async Task<User> CreateUserWithCreditAsync(AppDbContext db, int balance)
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

        // CreditPageTests sweeps every user and compares balance with ledger sum. Writing
        // only the ledger lets the database trigger derive valid test data in every order.
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<Game> CreateGameAsync(AppDbContext db, long ownerId)
    {
        var game = new Game
        {
            OwnerId = ownerId,
            Title = $"Test Game {Guid.NewGuid():N}",
        };

        db.Games.Add(game);
        await db.SaveChangesAsync();
        return game;
    }

    private static async Task<int> LedgerSumAsync(AppDbContext db, long userId) =>
        await db.CreditLedgerEntries
            .Where(entry => entry.UserId == userId)
            .SumAsync(
                entry => (int?)entry.Delta,
                TestContext.Current.CancellationToken) ?? 0;
}
