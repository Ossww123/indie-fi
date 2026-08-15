using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.KeyDistributions.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class KeyClaimServiceTests
{
    private readonly PostgreSqlFixture _fixture;

    public KeyClaimServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    // A drop must not grant a key before its opening time.
    [Fact]
    public async Task ClaimAsync_WhenDropOpensInFuture_ReturnsNotOpenWithoutCreatingGrant()
    {
        long userId;
        long dropId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            userId = (await CreateUserAsync(arrangeDb)).Id;
            dropId = (await CreateDropAsync(
                arrangeDb,
                DateTimeOffset.UtcNow.AddHours(1),
                keyCount: 1)).Id;
        }

        ClaimResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            var service = NewService(serviceDb);
            result = await service.ClaimAsync(
                dropId,
                userId,
                TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var grantCount = await verificationDb.KeyGrants
            .CountAsync(
                grant => grant.DropId == dropId,
                TestContext.Current.CancellationToken);

        Assert.Equal(ClaimOutcome.NotOpen, result.Outcome);
        Assert.Equal(0, grantCount);
    }

    [Fact]
    public async Task ClaimAsync_WhenDropClosedEarly_ReturnsClosedEarlyWithoutCreatingGrant()
    {
        long userId;
        long dropId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            userId = (await CreateUserAsync(arrangeDb)).Id;
            var drop = await CreateDropAsync(
                arrangeDb,
                DateTimeOffset.UtcNow.AddHours(-1),
                keyCount: 1);
            drop.ClosedAt = DateTimeOffset.UtcNow;
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            dropId = drop.Id;
        }

        ClaimResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await NewService(serviceDb).ClaimAsync(
                dropId,
                userId,
                TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var grantCount = await verificationDb.KeyGrants.CountAsync(
            grant => grant.DropId == dropId,
            TestContext.Current.CancellationToken);

        Assert.Equal(ClaimOutcome.ClosedEarly, result.Outcome);
        Assert.Equal(0, grantCount);
    }

    [Fact]
    public async Task ClaimAsync_WhenCurrentEligibilityIsNotMet_DoesNotCreateGrant()
    {
        long userId;
        long dropId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            userId = (await CreateUserAsync(arrangeDb)).Id;
            var drop = await CreateDropAsync(
                arrangeDb,
                DateTimeOffset.UtcNow.AddHours(-1),
                keyCount: 1);
            drop.MinimumAccountAgeDays = 1;
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            dropId = drop.Id;
        }

        ClaimResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await NewService(serviceDb).ClaimAsync(
                dropId,
                userId,
                TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        Assert.Equal(ClaimOutcome.NotEligible, result.Outcome);
        Assert.False(await verificationDb.KeyGrants.AnyAsync(
            grant => grant.DropId == dropId && grant.UserId == userId,
            TestContext.Current.CancellationToken));
    }

    // A user can receive only one key from the same drop.
    [Fact]
    public async Task ClaimAsync_WhenSameUserClaimsTwice_GrantsOnceThenReturnsAlreadyClaimed()
    {
        long userId;
        long dropId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            userId = (await CreateUserAsync(arrangeDb)).Id;
            dropId = (await CreateDropAsync(
                arrangeDb,
                DateTimeOffset.UtcNow.AddHours(-1),
                keyCount: 2)).Id;
        }

        ClaimResult firstResult;
        await using (var firstServiceDb = _fixture.NewDbContext())
        {
            var service = NewService(firstServiceDb);
            firstResult = await service.ClaimAsync(
                dropId,
                userId,
                TestContext.Current.CancellationToken);
        }

        ClaimResult secondResult;
        await using (var secondServiceDb = _fixture.NewDbContext())
        {
            var service = NewService(secondServiceDb);
            secondResult = await service.ClaimAsync(
                dropId,
                userId,
                TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var userGrantCount = await verificationDb.KeyGrants
            .CountAsync(
                grant => grant.DropId == dropId && grant.UserId == userId,
                TestContext.Current.CancellationToken);

        Assert.Equal(ClaimOutcome.Granted, firstResult.Outcome);
        Assert.Equal(ClaimOutcome.AlreadyClaimed, secondResult.Outcome);
        Assert.Equal(1, userGrantCount);
    }

    // Repeating a claim must preserve the original key and consume no extra key.
    [Fact]
    public async Task ClaimAsync_WhenClaimIsRepeated_KeepsOriginalKeyAndConsumesOnlyOneKey()
    {
        long userId;
        long dropId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            userId = (await CreateUserAsync(arrangeDb)).Id;
            dropId = (await CreateDropAsync(
                arrangeDb,
                DateTimeOffset.UtcNow.AddHours(-1),
                keyCount: 2)).Id;
        }

        ClaimResult firstResult;
        await using (var firstServiceDb = _fixture.NewDbContext())
        {
            var service = NewService(firstServiceDb);
            firstResult = await service.ClaimAsync(
                dropId,
                userId,
                TestContext.Current.CancellationToken);
        }

        long firstKeyId;
        await using (var afterFirstClaimDb = _fixture.NewDbContext())
        {
            firstKeyId = await afterFirstClaimDb.KeyGrants
                .Where(grant => grant.DropId == dropId && grant.UserId == userId)
                .Select(grant => grant.KeyId)
                .SingleAsync(TestContext.Current.CancellationToken);
        }

        ClaimResult secondResult;
        await using (var secondServiceDb = _fixture.NewDbContext())
        {
            var service = NewService(secondServiceDb);
            secondResult = await service.ClaimAsync(
                dropId,
                userId,
                TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var finalKeyId = await verificationDb.KeyGrants
            .Where(grant => grant.DropId == dropId && grant.UserId == userId)
            .Select(grant => grant.KeyId)
            .SingleAsync(TestContext.Current.CancellationToken);
        var remainingKeyCount = await verificationDb.GameKeys
            .CountAsync(key =>
                key.DropId == dropId &&
                !verificationDb.KeyGrants.Any(grant => grant.KeyId == key.Id),
                TestContext.Current.CancellationToken);

        Assert.Equal(ClaimOutcome.Granted, firstResult.Outcome);
        Assert.Equal(ClaimOutcome.AlreadyClaimed, secondResult.Outcome);
        Assert.Equal(firstKeyId, finalKeyId);
        Assert.Equal(1, remainingKeyCount);
    }

    // A drop must stop granting after every available key is assigned once.
    [Fact]
    public async Task ClaimAsync_WhenFourUsersClaimThreeKeys_GrantsThreeUniqueKeysThenReturnsSoldOut()
    {
        var userIds = new List<long>();
        long dropId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            for (var index = 0; index < 4; index++)
            {
                userIds.Add((await CreateUserAsync(arrangeDb)).Id);
            }

            dropId = (await CreateDropAsync(
                arrangeDb,
                DateTimeOffset.UtcNow.AddHours(-1),
                keyCount: 3)).Id;
        }

        var outcomes = new List<ClaimOutcome>();
        foreach (var userId in userIds)
        {
            await using var serviceDb = _fixture.NewDbContext();
            var service = NewService(serviceDb);
            outcomes.Add((await service.ClaimAsync(
                dropId,
                userId,
                TestContext.Current.CancellationToken)).Outcome);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var grants = await verificationDb.KeyGrants
            .Where(grant => grant.DropId == dropId)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                ClaimOutcome.Granted,
                ClaimOutcome.Granted,
                ClaimOutcome.Granted,
                ClaimOutcome.SoldOut
            ],
            outcomes);
        Assert.Equal(3, grants.Count);
        Assert.Equal(3, grants.Select(grant => grant.KeyId).Distinct().Count());
    }

    // When many users claim at the same instant and there are enough keys for all of
    // them, every claim must get a distinct key. Nobody should fail with Conflict while
    // keys are still free - that is the wasteful failure this drop is meant to avoid.
    [Fact]
    public async Task ClaimAsync_WhenManyUsersClaimAtOnceWithEnoughKeys_AllGetDistinctKeysWithoutConflict()
    {
        const int count = 20;
        var userIds = new List<long>();
        long dropId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            for (var index = 0; index < count; index++)
            {
                userIds.Add((await CreateUserAsync(arrangeDb)).Id);
            }

            dropId = (await CreateDropAsync(
                arrangeDb,
                DateTimeOffset.UtcNow.AddHours(-1),
                keyCount: count)).Id;
        }

        // Fire every claim at once, each on its own connection, so they truly race.
        var outcomes = await Task.WhenAll(userIds.Select(async userId =>
        {
            await using var serviceDb = _fixture.NewDbContext();
            return (await NewService(serviceDb).ClaimAsync(
                dropId,
                userId,
                TestContext.Current.CancellationToken)).Outcome;
        }));

        await using var verificationDb = _fixture.NewDbContext();
        var grantedKeyIds = await verificationDb.KeyGrants
            .Where(grant => grant.DropId == dropId)
            .Select(grant => grant.KeyId)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(count, outcomes.Count(outcome => outcome == ClaimOutcome.Granted));
        Assert.DoesNotContain(ClaimOutcome.Conflict, outcomes);
        Assert.Equal(count, grantedKeyIds.Distinct().Count());
    }

    [Fact]
    public async Task ClaimAsync_WhenManyUsersRaceForLastKey_GrantsExactlyOnce()
    {
        const int userCount = 20;
        var userIds = new List<long>();
        long dropId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            for (var index = 0; index < userCount; index++)
            {
                userIds.Add((await CreateUserAsync(arrangeDb)).Id);
            }

            dropId = (await CreateDropAsync(
                arrangeDb,
                DateTimeOffset.UtcNow.AddHours(-1),
                keyCount: 1)).Id;
        }

        var outcomes = await Task.WhenAll(userIds.Select(async userId =>
        {
            await using var serviceDb = _fixture.NewDbContext();
            return (await NewService(serviceDb).ClaimAsync(
                dropId,
                userId,
                TestContext.Current.CancellationToken)).Outcome;
        }));

        await using var verificationDb = _fixture.NewDbContext();
        var grants = await verificationDb.KeyGrants
            .Where(grant => grant.DropId == dropId)
            .ToListAsync(TestContext.Current.CancellationToken);
        var stateVersion = await verificationDb.KeyDrops
            .Where(drop => drop.Id == dropId)
            .Select(drop => drop.StateVersion)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, outcomes.Count(outcome => outcome == ClaimOutcome.Granted));
        Assert.Equal(
            userCount - 1,
            outcomes.Count(outcome => outcome == ClaimOutcome.SoldOut));
        Assert.DoesNotContain(ClaimOutcome.Conflict, outcomes);
        Assert.Single(grants);
        Assert.Equal(1, stateVersion);
    }

    // Concurrent duplicate submissions cannot be resolved by retrying with another key.
    // A duplicate-user unique violation must be reported immediately as AlreadyClaimed.
    [Fact]
    public async Task ClaimAsync_WhenOneUserClaimsManyTimesAtOnce_GrantsOnceAndReportsAlreadyClaimed()
    {
        long userId;
        long dropId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            userId = (await CreateUserAsync(arrangeDb)).Id;
            dropId = (await CreateDropAsync(
                arrangeDb,
                DateTimeOffset.UtcNow.AddHours(-1),
                keyCount: 5)).Id;
        }

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var serviceDb = _fixture.NewDbContext();
            return (await NewService(serviceDb).ClaimAsync(
                dropId,
                userId,
                TestContext.Current.CancellationToken)).Outcome;
        }));

        await using var verificationDb = _fixture.NewDbContext();
        var userGrantCount = await verificationDb.KeyGrants
            .CountAsync(
                grant => grant.DropId == dropId && grant.UserId == userId,
                TestContext.Current.CancellationToken);

        Assert.Equal(1, outcomes.Count(outcome => outcome == ClaimOutcome.Granted));
        Assert.Equal(0, outcomes.Count(outcome => outcome == ClaimOutcome.Conflict));
        Assert.Equal(1, userGrantCount);
    }

    // Measurements showed that database SQL execution time was negligible while round trips were costly.
    // This count is a budget: adding another query later must fail this test.
    // We count round trips instead of p95 latency because front-end saturation hides latency on a development laptop.
    [Fact]
    public async Task ClaimAsync_WhenGrantingAKey_MakesTheExpectedNumberOfDatabaseRoundTrips()
    {
        // Explicit serialization needs BEGIN + lock/precheck + mutation + COMMIT.
        const int ExpectedRoundTrips = 4;
        long userId;
        long dropId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            userId = (await CreateUserAsync(arrangeDb)).Id;
            dropId = (await CreateDropAsync(
                arrangeDb,
                DateTimeOffset.UtcNow.AddHours(-1),
                keyCount: 3)).Id;
        }

        var interceptor = new RoundTripCountingInterceptor();
        ClaimResult result;
        await using (var serviceDb = _fixture.NewDbContext(interceptor))
        {
            var service = NewService(serviceDb);
            result = await service.ClaimAsync(
                dropId,
                userId,
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(ClaimOutcome.Granted, result.Outcome);
        Assert.Equal(ExpectedRoundTrips, interceptor.Count);
    }

    // The recently rewritten missing-drop path was the only claim outcome without coverage.
    [Fact]
    public async Task ClaimAsync_WhenDropDoesNotExist_ReturnsDropNotFoundWithoutCreatingGrant()
    {
        const long DropId = long.MaxValue;
        long userId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            userId = (await CreateUserAsync(arrangeDb)).Id;
        }

        ClaimResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            var service = NewService(serviceDb);
            result = await service.ClaimAsync(
                DropId,
                userId,
                TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var grantCount = await verificationDb.KeyGrants
            .CountAsync(
                grant => grant.DropId == DropId,
                TestContext.Current.CancellationToken);

        Assert.Equal(ClaimOutcome.DropNotFound, result.Outcome);
        Assert.Equal(0, grantCount);
    }

    // The service takes a logger it uses for one line per claim; tests do not assert on
    // logs, so a no-op logger keeps the call sites readable.
    private static KeyClaimService NewService(AppDbContext db) =>
        new(
            db,
            new KeyDropMutationLock(db),
            NullLogger<KeyClaimService>.Instance);

    private static async Task<User> CreateUserAsync(AppDbContext db)
    {
        var user = TestAccountProtection.AddUser(
            db,
            $"{Guid.NewGuid():N}@example.test");
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<KeyDrop> CreateDropAsync(
        AppDbContext db,
        DateTimeOffset opensAt,
        int keyCount)
    {
        var owner = await CreateUserAsync(db);
        var game = new Game
        {
            OwnerId = owner.Id,
            Title = $"Test Game {Guid.NewGuid():N}"
        };

        db.Games.Add(game);
        await db.SaveChangesAsync();

        var drop = new KeyDrop
        {
            GameId = game.Id,
            OwnerId = owner.Id,
            OpensAt = opensAt,
            TotalKeys = keyCount
        };

        db.KeyDrops.Add(drop);
        await db.SaveChangesAsync();

        for (var index = 0; index < keyCount; index++)
        {
            TestAccountProtection.AddGameKey(
                db,
                drop,
                $"TEST-{Guid.NewGuid():N}");
        }

        await db.SaveChangesAsync();
        return drop;
    }
}
