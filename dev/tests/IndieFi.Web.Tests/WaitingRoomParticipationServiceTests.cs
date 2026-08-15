using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.KeyDistributions.Application;
using IndieFi.Web.Modules.KeyDistributions.Domain;
using IndieFi.Web.Modules.KeyDistributions.Infrastructure;
using Microsoft.Extensions.Options;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class WaitingRoomParticipationServiceTests
{
    private readonly PostgreSqlFixture _fixture;

    public WaitingRoomParticipationServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task JoinAsync_BeforeThirtyMinuteWindow_DoesNotWriteRedis()
    {
        var arranged = await CreateDropAsync(TimeSpan.FromMinutes(31));
        var store = new RecordingWaitingRoomStore();

        await using var db = _fixture.NewDbContext();
        var result = await NewService(db, store).JoinAsync(
            arranged.DropId,
            arranged.UserId,
            TestContext.Current.CancellationToken);

        Assert.Equal(WaitingRoomParticipationOutcome.NotYetOpen, result.Outcome);
        Assert.Null(store.LastJoinCommand);
    }

    [Fact]
    public async Task JoinAsync_InsidePreOpenWindow_UsesPreWaitingMode()
    {
        var arranged = await CreateDropAsync(TimeSpan.FromMinutes(20));
        var store = new RecordingWaitingRoomStore();

        await using var db = _fixture.NewDbContext();
        var result = await NewService(db, store).JoinAsync(
            arranged.DropId,
            arranged.UserId,
            TestContext.Current.CancellationToken);

        Assert.Equal(WaitingRoomParticipationOutcome.Joined, result.Outcome);
        Assert.NotNull(store.LastJoinCommand);
        Assert.Equal(WaitingRoomJoinMode.PreOpen, store.LastJoinCommand.Mode);
        Assert.Equal(WaitingRoomEntryState.PreWaiting, result.Entry!.State);
    }

    [Fact]
    public async Task JoinAsync_AfterOpen_UsesOpenQueueMode()
    {
        var arranged = await CreateDropAsync(TimeSpan.FromMinutes(-1));
        var store = new RecordingWaitingRoomStore();

        await using var db = _fixture.NewDbContext();
        var result = await NewService(db, store).JoinAsync(
            arranged.DropId,
            arranged.UserId,
            TestContext.Current.CancellationToken);

        Assert.Equal(WaitingRoomParticipationOutcome.Joined, result.Outcome);
        Assert.Equal(WaitingRoomJoinMode.Open, store.LastJoinCommand!.Mode);
        Assert.Equal(WaitingRoomEntryState.Waiting, result.Entry!.State);
    }

    [Fact]
    public async Task JoinAsync_WhenEligibilityIsNotMet_DoesNotWriteRedis()
    {
        var arranged = await CreateDropAsync(
            TimeSpan.FromMinutes(20),
            minimumAccountAgeDays: 1);
        var store = new RecordingWaitingRoomStore();

        await using var db = _fixture.NewDbContext();
        var result = await NewService(db, store).JoinAsync(
            arranged.DropId,
            arranged.UserId,
            TestContext.Current.CancellationToken);

        Assert.Equal(WaitingRoomParticipationOutcome.NotEligible, result.Outcome);
        Assert.Null(store.LastJoinCommand);
    }

    [Fact]
    public async Task GetAsync_WithExistingEntry_ReturnsSameStateWithoutJoiningAgain()
    {
        var arranged = await CreateDropAsync(TimeSpan.FromMinutes(20));
        var existing = new WaitingRoomStoredEntry(
            WaitingRoomEntryState.PreWaiting,
            Order: 11,
            LastSeenUnixTimeMilliseconds: 1234);
        var store = new RecordingWaitingRoomStore
        {
            ExistingEntry = existing,
        };

        await using var db = _fixture.NewDbContext();
        var result = await NewService(db, store).GetAsync(
            arranged.DropId,
            arranged.UserId,
            TestContext.Current.CancellationToken);

        Assert.Equal(WaitingRoomParticipationOutcome.Existing, result.Outcome);
        Assert.Equal(existing, result.Entry);
        Assert.Null(store.LastJoinCommand);
    }

    private async Task<(long DropId, long UserId)> CreateDropAsync(
        TimeSpan opensAfter,
        int? minimumAccountAgeDays = null)
    {
        await using var db = _fixture.NewDbContext();
        var user = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var owner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var game = await ReviewTestData.CreateGameAsync(db, owner.Id);
        var drop = new KeyDrop
        {
            GameId = game.Id,
            OwnerId = owner.Id,
            OpensAt = DateTimeOffset.UtcNow.Add(opensAfter),
            TotalKeys = 1,
            MinimumAccountAgeDays = minimumAccountAgeDays,
        };
        db.KeyDrops.Add(drop);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        TestAccountProtection.AddGameKey(
            db,
            drop,
            $"WAITING-ROOM-{Guid.NewGuid():N}");
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (drop.Id, user.Id);
    }

    private static WaitingRoomParticipationService NewService(
        AppDbContext db,
        IWaitingRoomStore store) =>
        new(
            db,
            new KeyDropEligibilityService(db),
            store,
            Options.Create(new WaitingRoomPolicyOptions()));

    private sealed class RecordingWaitingRoomStore : IWaitingRoomStore
    {
        public WaitingRoomJoinCommand? LastJoinCommand { get; private set; }

        public WaitingRoomStoredEntry? ExistingEntry { get; init; }

        public Task<WaitingRoomJoinResult> JoinAsync(
            WaitingRoomJoinCommand command,
            CancellationToken cancellationToken = default)
        {
            LastJoinCommand = command;
            var state = command.Mode == WaitingRoomJoinMode.PreOpen
                ? WaitingRoomEntryState.PreWaiting
                : WaitingRoomEntryState.Waiting;
            return Task.FromResult(new WaitingRoomJoinResult(
                WaitingRoomJoinOutcome.Created,
                new WaitingRoomStoredEntry(state, 1, 1000)));
        }

        public Task<WaitingRoomStoredEntry?> GetEntryAsync(
            long dropId,
            long userId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ExistingEntry);

        public Task<WaitingRoomTransitionResult> TransitionAsync(
            WaitingRoomTransitionCommand command,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WaitingRoomOrderFinalizationResult> FinalizeOrderAsync(
            long dropId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WaitingRoomOrderFinalizationResult(
                WaitingRoomOrderFinalizationOutcome.AlreadyFinalized,
                0));
    }
}
