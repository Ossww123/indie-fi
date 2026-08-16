using IndieFi.Web.Entities;
using IndieFi.Web.Modules.KeyDistributions.Application;
using IndieFi.Web.Modules.KeyDistributions.Domain;
using IndieFi.Web.Modules.KeyDistributions.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlRedisCollection.Name)]
public sealed class WaitingRoomLifecycleIntegrationTests
{
    private readonly PostgreSqlFixture _postgresql;
    private readonly RedisFixture _redis;

    public WaitingRoomLifecycleIntegrationTests(
        PostgreSqlFixture postgresql,
        RedisFixture redis)
    {
        _postgresql = postgresql;
        _redis = redis;
    }

    [Fact]
    public async Task LastKeyClaim_TerminatesOthersAndPreservesClaimantAsCompleted()
    {
        var arranged = await CreateActiveDropAsync();
        var store = _redis.NewStore(new WaitingRoomPolicyOptions
        {
            ConcurrentAdmissionLimit = 1,
        });
        await JoinAndOpenAsync(store, arranged.DropId, arranged.UserIds);
        var admission = await store.AdvanceAdmissionsAsync(
            arranged.DropId,
            TestContext.Current.CancellationToken);
        var claimantId = Assert.Single(admission.PromotedUserIds);
        var otherUserId = arranged.UserIds.Single(userId => userId != claimantId);
        var lifecycle = NewLifecycle(store);
        var gate = new WaitingRoomKeyClaimAdmissionGate(
            store,
            NullWaitingRoomNotifier.Instance);

        ClaimResult claim;
        await using (var claimDb = _postgresql.NewDbContext())
        {
            claim = await new KeyClaimService(
                    claimDb,
                    new KeyDropMutationLock(claimDb),
                    NullLogger<KeyClaimService>.Instance,
                    gate,
                    lifecycle)
                .ClaimAsync(
                    arranged.DropId,
                    claimantId,
                    TestContext.Current.CancellationToken);
        }

        var claimant = await store.GetEntryAsync(
            arranged.DropId,
            claimantId,
            TestContext.Current.CancellationToken);
        var other = await store.GetEntryAsync(
            arranged.DropId,
            otherUserId,
            TestContext.Current.CancellationToken);
        var advance = await store.AdvanceAdmissionsAsync(
            arranged.DropId,
            TestContext.Current.CancellationToken);

        Assert.Equal(ClaimOutcome.Granted, claim.Outcome);
        Assert.Equal(WaitingRoomEntryState.Completed, claimant!.State);
        Assert.Equal(WaitingRoomEntryState.Terminal, other!.State);
        Assert.Equal(WaitingRoomAdmissionAdvanceOutcome.Terminal, advance.Outcome);
    }

    [Fact]
    public async Task LaterReadAndRepeatedClaim_RepairMissedTerminalAndCompletionSync()
    {
        var arranged = await CreateActiveDropAsync();
        var store = _redis.NewStore();
        await JoinAndOpenAsync(store, arranged.DropId, arranged.UserIds);
        var claimantId = arranged.UserIds[0];
        var waitingUserId = arranged.UserIds[1];

        await using (var claimDb = _postgresql.NewDbContext())
        {
            var claim = await new KeyClaimService(
                    claimDb,
                    new KeyDropMutationLock(claimDb),
                    NullLogger<KeyClaimService>.Instance,
                    AllowKeyClaimAdmissionGate.Instance,
                    NullWaitingRoomLifecycle.Instance)
                .ClaimAsync(
                    arranged.DropId,
                    claimantId,
                    TestContext.Current.CancellationToken);
            Assert.Equal(ClaimOutcome.Granted, claim.Outcome);
        }

        var lifecycle = NewLifecycle(store);
        WaitingRoomParticipationResult participation;
        await using (var participationDb = _postgresql.NewDbContext())
        {
            participation = await new WaitingRoomParticipationService(
                    participationDb,
                    new KeyDropEligibilityService(participationDb),
                    store,
                    NullWaitingRoomNotifier.Instance,
                    lifecycle,
                    Options.Create(new WaitingRoomPolicyOptions()))
                .GetAsync(
                    arranged.DropId,
                    waitingUserId,
                    TestContext.Current.CancellationToken);
        }

        ClaimResult repeated;
        await using (var repeatedDb = _postgresql.NewDbContext())
        {
            repeated = await new KeyClaimService(
                    repeatedDb,
                    new KeyDropMutationLock(repeatedDb),
                    NullLogger<KeyClaimService>.Instance,
                    new WaitingRoomKeyClaimAdmissionGate(
                        store,
                        NullWaitingRoomNotifier.Instance),
                    lifecycle)
                .ClaimAsync(
                    arranged.DropId,
                    claimantId,
                    TestContext.Current.CancellationToken);
        }

        var waiting = await store.GetEntryAsync(
            arranged.DropId,
            waitingUserId,
            TestContext.Current.CancellationToken);
        var claimant = await store.GetEntryAsync(
            arranged.DropId,
            claimantId,
            TestContext.Current.CancellationToken);

        Assert.Equal(WaitingRoomParticipationOutcome.SoldOut, participation.Outcome);
        Assert.Equal(WaitingRoomEntryState.Terminal, waiting!.State);
        Assert.Equal(ClaimOutcome.AlreadyClaimed, repeated.Outcome);
        Assert.Equal(WaitingRoomEntryState.Completed, claimant!.State);
    }

    private WaitingRoomLifecycle NewLifecycle(IWaitingRoomStore store) =>
        new(
            store,
            NullWaitingRoomNotifier.Instance,
            NullLogger<WaitingRoomLifecycle>.Instance);

    private async Task<DropData> CreateActiveDropAsync()
    {
        await using var db = _postgresql.NewDbContext();
        var first = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var second = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var owner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var game = await ReviewTestData.CreateGameAsync(db, owner.Id);
        var drop = new KeyDrop
        {
            GameId = game.Id,
            OwnerId = owner.Id,
            OpensAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            TotalKeys = 1,
        };
        db.KeyDrops.Add(drop);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        TestAccountProtection.AddGameKey(
            db,
            drop,
            $"WAITING-LIFECYCLE-{Guid.NewGuid():N}");
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return new DropData(drop.Id, [first.Id, second.Id]);
    }

    private static async Task JoinAndOpenAsync(
        IWaitingRoomStore store,
        long dropId,
        IReadOnlyList<long> userIds)
    {
        await Task.WhenAll(userIds.Select(userId => store.JoinAsync(
            new WaitingRoomJoinCommand(dropId, userId, WaitingRoomJoinMode.Open),
            TestContext.Current.CancellationToken)));
        await store.FinalizeOrderAsync(
            dropId,
            TestContext.Current.CancellationToken);
    }

    private sealed record DropData(long DropId, IReadOnlyList<long> UserIds);
}

[CollectionDefinition(Name)]
public sealed class PostgreSqlRedisCollection :
    ICollectionFixture<PostgreSqlFixture>,
    ICollectionFixture<RedisFixture>
{
    public const string Name = "PostgreSQL and Redis";
}
