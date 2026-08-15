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
    public async Task JoinAsync_AfterOpen_AdvancesSlotAndReturnsLatestSnapshot()
    {
        var arranged = await CreateDropAsync(TimeSpan.FromMinutes(-1));
        var store = new RecordingWaitingRoomStore
        {
            AdmitOnAdvance = true,
        };

        var notifier = new RecordingWaitingRoomNotifier();
        await using var db = _fixture.NewDbContext();
        var result = await NewService(db, store, notifier).JoinAsync(
            arranged.DropId,
            arranged.UserId,
            TestContext.Current.CancellationToken);

        Assert.Equal(WaitingRoomParticipationOutcome.Joined, result.Outcome);
        Assert.Equal(1, store.AdvanceCalls);
        Assert.Equal(WaitingRoomEntryState.Admitted, result.Entry!.State);
        Assert.NotNull(result.Entry.AdmissionExpiresUnixTimeMilliseconds);
        Assert.Equal(1, result.Snapshot!.AdmittedCount);
        Assert.Equal(0, result.Snapshot.WaitingCount);
        Assert.True(notifier.DropNotifications >= 1);
        Assert.Contains(arranged.UserId, notifier.UserNotifications);
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
        Assert.NotNull(result.Snapshot);
        Assert.Equal(1, result.Snapshot.PreWaitingCount);
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
        IWaitingRoomStore store,
        IWaitingRoomNotifier? notifier = null) =>
        new(
            db,
            new KeyDropEligibilityService(db),
            store,
            notifier ?? NullWaitingRoomNotifier.Instance,
            NullWaitingRoomLifecycle.Instance,
            Options.Create(new WaitingRoomPolicyOptions()));

    private sealed class RecordingWaitingRoomNotifier : IWaitingRoomNotifier
    {
        public int DropNotifications { get; private set; }

        public List<long> UserNotifications { get; } = [];

        public Task NotifyDropChangedAsync(
            long dropId,
            CancellationToken cancellationToken = default)
        {
            DropNotifications++;
            return Task.CompletedTask;
        }

        public Task NotifyUsersChangedAsync(
            long dropId,
            IReadOnlyCollection<long> userIds,
            CancellationToken cancellationToken = default)
        {
            UserNotifications.AddRange(userIds);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingWaitingRoomStore : IWaitingRoomStore
    {
        public WaitingRoomJoinCommand? LastJoinCommand { get; private set; }

        public WaitingRoomStoredEntry? ExistingEntry { get; set; }

        public bool AdmitOnAdvance { get; init; }

        public int AdvanceCalls { get; private set; }

        public Task<WaitingRoomJoinResult> JoinAsync(
            WaitingRoomJoinCommand command,
            CancellationToken cancellationToken = default)
        {
            LastJoinCommand = command;
            var state = command.Mode == WaitingRoomJoinMode.PreOpen
                ? WaitingRoomEntryState.PreWaiting
                : WaitingRoomEntryState.Waiting;
            ExistingEntry ??= new WaitingRoomStoredEntry(state, 1, 1000);
            return Task.FromResult(new WaitingRoomJoinResult(
                WaitingRoomJoinOutcome.Created,
                ExistingEntry));
        }

        public Task<WaitingRoomStoredEntry?> GetEntryAsync(
            long dropId,
            long userId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ExistingEntry);

        public Task<WaitingRoomSnapshot?> GetSnapshotAsync(
            long dropId,
            long userId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ExistingEntry is null
                ? null
                : new WaitingRoomSnapshot(
                    ExistingEntry,
                    Position: ExistingEntry.State == WaitingRoomEntryState.Waiting ? 1 : null,
                    AheadCount: ExistingEntry.State == WaitingRoomEntryState.Waiting ? 0 : null,
                    WaitingCount: ExistingEntry.State == WaitingRoomEntryState.Waiting ? 1 : 0,
                    PreWaitingCount: ExistingEntry.State == WaitingRoomEntryState.PreWaiting ? 1 : 0,
                    AdmittedCount: ExistingEntry.State == WaitingRoomEntryState.Admitted ? 1 : 0,
                    OrderFinalized: ExistingEntry.State != WaitingRoomEntryState.PreWaiting));

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

        public Task<WaitingRoomAdmissionAdvanceResult> AdvanceAdmissionsAsync(
            long dropId,
            CancellationToken cancellationToken = default)
        {
            AdvanceCalls++;
            IReadOnlyList<long> promoted = [];
            if (AdmitOnAdvance && ExistingEntry?.State == WaitingRoomEntryState.Waiting)
            {
                ExistingEntry = ExistingEntry with
                {
                    State = WaitingRoomEntryState.Admitted,
                    AdmissionExpiresUnixTimeMilliseconds = DateTimeOffset.UtcNow
                        .AddMinutes(2)
                        .ToUnixTimeMilliseconds(),
                };
                promoted = LastJoinCommand is null ? [] : [LastJoinCommand.UserId];
            }

            return Task.FromResult(new WaitingRoomAdmissionAdvanceResult(
                WaitingRoomAdmissionAdvanceOutcome.Advanced,
                promoted,
                [],
                ExistingEntry?.State == WaitingRoomEntryState.Admitted ? 1 : 0));
        }

        public Task<WaitingRoomAdmissionValidationResult> ValidateAdmissionAsync(
            long dropId,
            long userId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WaitingRoomCompletionResult> CompleteAsync(
            long dropId,
            long userId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WaitingRoomTerminationResult> TerminateAsync(
            long dropId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
