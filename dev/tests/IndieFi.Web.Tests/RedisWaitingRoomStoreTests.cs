using IndieFi.Web.Modules.KeyDistributions.Application;
using IndieFi.Web.Modules.KeyDistributions.Domain;
using IndieFi.Web.Modules.KeyDistributions.Infrastructure;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(RedisCollection.Name)]
public sealed class RedisWaitingRoomStoreTests
{
    private static long _nextDropId = 1000;
    private readonly RedisFixture _fixture;

    public RedisWaitingRoomStoreTests(RedisFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task JoinAsync_WhenSameUserJoinsConcurrently_CreatesOneEntry()
    {
        var dropId = NextDropId();
        var store = _fixture.NewStore();
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            store.JoinAsync(
                new WaitingRoomJoinCommand(dropId, 51, WaitingRoomJoinMode.Open),
                TestContext.Current.CancellationToken)));

        var database = await _fixture.GetDatabaseAsync();
        var entriesLength = await database.HashLengthAsync(
            _fixture.GetKeys().Entries(dropId));
        var queueLength = await database.SortedSetLengthAsync(
            _fixture.GetKeys().Waiting(dropId));

        Assert.Equal(1, results.Count(result => result.Outcome == WaitingRoomJoinOutcome.Created));
        Assert.Equal(19, results.Count(result => result.Outcome == WaitingRoomJoinOutcome.Existing));
        Assert.All(results, result =>
        {
            Assert.NotNull(result.Entry);
            Assert.Equal(1, result.Entry.Order);
            Assert.Equal(WaitingRoomEntryState.Waiting, result.Entry.State);
        });
        Assert.Equal(1, entriesLength);
        Assert.Equal(1, queueLength);
    }

    [Fact]
    public async Task JoinAsync_WhenManyUsersJoinConcurrently_AssignsUniqueMonotonicOrders()
    {
        const int UserCount = 50;
        var dropId = NextDropId();
        var store = _fixture.NewStore();
        var results = await Task.WhenAll(Enumerable.Range(1, UserCount).Select(userId =>
            store.JoinAsync(
                new WaitingRoomJoinCommand(
                    dropId,
                    userId,
                    WaitingRoomJoinMode.PreOpen),
                TestContext.Current.CancellationToken)));

        var orders = results.Select(result => result.Entry!.Order).Order().ToArray();
        var database = await _fixture.GetDatabaseAsync();

        Assert.All(results, result => Assert.Equal(
            WaitingRoomJoinOutcome.Created,
            result.Outcome));
        Assert.Equal(Enumerable.Range(1, UserCount).Select(value => (long)value), orders);
        Assert.Equal(
            UserCount,
            await database.SortedSetLengthAsync(_fixture.GetKeys().PreWaiting(dropId)));
        Assert.Equal(
            UserCount,
            await database.HashLengthAsync(_fixture.GetKeys().Entries(dropId)));
    }

    [Fact]
    public async Task JoinAsync_AfterReconnectGrace_RejoinsOpenQueueAtTail()
    {
        var dropId = NextDropId();
        var policy = new WaitingRoomPolicyOptions
        {
            ReconnectGrace = TimeSpan.FromMilliseconds(100),
        };
        var store = _fixture.NewStore(policy);
        var first = await store.JoinAsync(
            new WaitingRoomJoinCommand(dropId, 1, WaitingRoomJoinMode.Open),
            TestContext.Current.CancellationToken);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        var second = await store.JoinAsync(
            new WaitingRoomJoinCommand(dropId, 2, WaitingRoomJoinMode.Open),
            TestContext.Current.CancellationToken);
        var rejoined = await store.JoinAsync(
            new WaitingRoomJoinCommand(dropId, 1, WaitingRoomJoinMode.Open),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, first.Entry!.Order);
        Assert.Equal(2, second.Entry!.Order);
        Assert.Equal(WaitingRoomJoinOutcome.Rejoined, rejoined.Outcome);
        Assert.Equal(3, rejoined.Entry!.Order);
        Assert.Equal(WaitingRoomEntryState.Waiting, rejoined.Entry.State);
    }

    [Fact]
    public async Task TransitionAsync_WhenRequestsRace_ChangesExpectedStateOnce()
    {
        var dropId = NextDropId();
        const long UserId = 77;
        var store = _fixture.NewStore();
        await store.JoinAsync(
            new WaitingRoomJoinCommand(dropId, UserId, WaitingRoomJoinMode.PreOpen),
            TestContext.Current.CancellationToken);

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            store.TransitionAsync(
                new WaitingRoomTransitionCommand(
                    dropId,
                    UserId,
                    WaitingRoomEntryState.PreWaiting,
                    WaitingRoomEntryState.Admitted),
                TestContext.Current.CancellationToken)));
        var stored = await store.GetEntryAsync(
            dropId,
            UserId,
            TestContext.Current.CancellationToken);
        var database = await _fixture.GetDatabaseAsync();

        Assert.Equal(
            1,
            results.Count(result => result.Outcome == WaitingRoomTransitionOutcome.Changed));
        Assert.Equal(
            11,
            results.Count(result => result.Outcome == WaitingRoomTransitionOutcome.StateMismatch));
        Assert.NotNull(stored);
        Assert.Equal(WaitingRoomEntryState.Admitted, stored.State);
        Assert.NotNull(stored.AdmissionExpiresUnixTimeMilliseconds);
        Assert.Equal(
            (long)TimeSpan.FromMinutes(2).TotalMilliseconds,
            stored.AdmissionExpiresUnixTimeMilliseconds.Value
            - stored.LastSeenUnixTimeMilliseconds);
        Assert.Equal(
            0,
            await database.SortedSetLengthAsync(_fixture.GetKeys().PreWaiting(dropId)));
        Assert.Equal(
            1,
            await database.SortedSetLengthAsync(_fixture.GetKeys().Admitted(dropId)));
    }

    [Fact]
    public async Task FinalizeOrderAsync_RandomizesPreWaitingOnceAndKeepsOpenJoinsBehindIt()
    {
        const int PreWaitingCount = 20;
        var dropId = NextDropId();
        var store = _fixture.NewStore();
        await Task.WhenAll(Enumerable.Range(1, PreWaitingCount).Select(userId =>
            store.JoinAsync(
                new WaitingRoomJoinCommand(
                    dropId,
                    userId,
                    WaitingRoomJoinMode.PreOpen),
                TestContext.Current.CancellationToken)));
        await store.JoinAsync(
            new WaitingRoomJoinCommand(dropId, 100, WaitingRoomJoinMode.Open),
            TestContext.Current.CancellationToken);

        var finalizations = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            store.FinalizeOrderAsync(
                dropId,
                TestContext.Current.CancellationToken)));
        var entries = await Task.WhenAll(
            Enumerable.Range(1, PreWaitingCount)
                .Append(100)
                .Select(userId => store.GetEntryAsync(
                    dropId,
                    userId,
                    TestContext.Current.CancellationToken)));
        var tail = entries[^1]!;
        var firstOrders = entries.Select(entry => entry!.Order).ToArray();

        var repeated = await store.FinalizeOrderAsync(
            dropId,
            TestContext.Current.CancellationToken);
        var postOpen = await store.JoinAsync(
            new WaitingRoomJoinCommand(dropId, 101, WaitingRoomJoinMode.Open),
            TestContext.Current.CancellationToken);
        var database = await _fixture.GetDatabaseAsync();

        Assert.Equal(
            1,
            finalizations.Count(result =>
                result.Outcome == WaitingRoomOrderFinalizationOutcome.Finalized));
        Assert.Equal(
            9,
            finalizations.Count(result =>
                result.Outcome == WaitingRoomOrderFinalizationOutcome.AlreadyFinalized));
        Assert.All(entries, entry => Assert.Equal(
            WaitingRoomEntryState.Waiting,
            entry!.State));
        Assert.Equal(
            Enumerable.Range(1, PreWaitingCount + 1).Select(value => (long)value),
            firstOrders.Order());
        Assert.Equal(PreWaitingCount + 1, tail.Order);
        Assert.Equal(
            WaitingRoomOrderFinalizationOutcome.AlreadyFinalized,
            repeated.Outcome);
        Assert.Equal(PreWaitingCount + 2, postOpen.Entry!.Order);
        Assert.Equal(
            0,
            await database.SortedSetLengthAsync(_fixture.GetKeys().PreWaiting(dropId)));
        Assert.Equal(
            PreWaitingCount + 2,
            await database.SortedSetLengthAsync(_fixture.GetKeys().Waiting(dropId)));
    }

    [Fact]
    public async Task GetSnapshotAsync_ReturnsOneAtomicViewBeforeAndAfterFinalization()
    {
        var dropId = NextDropId();
        var store = _fixture.NewStore();
        await store.JoinAsync(
            new WaitingRoomJoinCommand(dropId, 1, WaitingRoomJoinMode.PreOpen),
            TestContext.Current.CancellationToken);
        await store.JoinAsync(
            new WaitingRoomJoinCommand(dropId, 2, WaitingRoomJoinMode.PreOpen),
            TestContext.Current.CancellationToken);

        var before = await store.GetSnapshotAsync(
            dropId,
            1,
            TestContext.Current.CancellationToken);
        await store.FinalizeOrderAsync(
            dropId,
            TestContext.Current.CancellationToken);
        var after = await store.GetSnapshotAsync(
            dropId,
            1,
            TestContext.Current.CancellationToken);

        Assert.NotNull(before);
        Assert.Equal(WaitingRoomEntryState.PreWaiting, before.Entry.State);
        Assert.Null(before.Position);
        Assert.Null(before.AheadCount);
        Assert.Equal(2, before.PreWaitingCount);
        Assert.False(before.OrderFinalized);

        Assert.NotNull(after);
        Assert.Equal(WaitingRoomEntryState.Waiting, after.Entry.State);
        Assert.NotNull(after.Position);
        Assert.Equal(after.Position - 1, after.AheadCount);
        Assert.Equal(2, after.WaitingCount);
        Assert.Equal(0, after.PreWaitingCount);
        Assert.True(after.OrderFinalized);
    }

    [Fact]
    public async Task AdvanceAdmissionsAsync_WhenCalledConcurrently_AdmitsOnlyConfiguredLimitInOrder()
    {
        const int UserCount = 25;
        var dropId = NextDropId();
        var store = _fixture.NewStore();
        await Task.WhenAll(Enumerable.Range(1, UserCount).Select(userId =>
            store.JoinAsync(
                new WaitingRoomJoinCommand(
                    dropId,
                    userId,
                    WaitingRoomJoinMode.PreOpen),
                TestContext.Current.CancellationToken)));
        await store.FinalizeOrderAsync(
            dropId,
            TestContext.Current.CancellationToken);

        var advances = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            store.AdvanceAdmissionsAsync(
                dropId,
                TestContext.Current.CancellationToken)));
        var entries = await Task.WhenAll(Enumerable.Range(1, UserCount).Select(userId =>
            store.GetEntryAsync(
                dropId,
                userId,
                TestContext.Current.CancellationToken)));
        var admitted = entries.Where(entry => entry!.State == WaitingRoomEntryState.Admitted)
            .ToArray();
        var waiting = entries.Where(entry => entry!.State == WaitingRoomEntryState.Waiting)
            .ToArray();

        Assert.Equal(10, advances.Sum(result => result.PromotedUserIds.Count));
        Assert.All(advances, result => Assert.InRange(result.AdmittedCount, 10, 10));
        Assert.Equal(10, admitted.Length);
        Assert.Equal(15, waiting.Length);
        Assert.All(admitted, entry => Assert.InRange(entry!.Order, 1, 10));
        Assert.All(waiting, entry => Assert.InRange(entry!.Order, 11, UserCount));
    }

    [Fact]
    public async Task AdvanceAdmissionsAsync_ExpiresPermitsAndPromotesNextWithoutAutoRequeue()
    {
        var dropId = NextDropId();
        var policy = new WaitingRoomPolicyOptions
        {
            ConcurrentAdmissionLimit = 2,
            AdmissionLifetime = TimeSpan.FromMilliseconds(100),
        };
        var store = _fixture.NewStore(policy);
        await Task.WhenAll(Enumerable.Range(1, 4).Select(userId =>
            store.JoinAsync(
                new WaitingRoomJoinCommand(
                    dropId,
                    userId,
                    WaitingRoomJoinMode.PreOpen),
                TestContext.Current.CancellationToken)));
        await store.FinalizeOrderAsync(
            dropId,
            TestContext.Current.CancellationToken);
        var firstAdvance = await store.AdvanceAdmissionsAsync(
            dropId,
            TestContext.Current.CancellationToken);
        await Task.Delay(150, TestContext.Current.CancellationToken);

        var secondAdvance = await store.AdvanceAdmissionsAsync(
            dropId,
            TestContext.Current.CancellationToken);
        var expiredEntries = await Task.WhenAll(firstAdvance.PromotedUserIds.Select(userId =>
            store.GetEntryAsync(
                dropId,
                userId,
                TestContext.Current.CancellationToken)));
        var promotedEntries = await Task.WhenAll(secondAdvance.PromotedUserIds.Select(userId =>
            store.GetEntryAsync(
                dropId,
                userId,
                TestContext.Current.CancellationToken)));
        var rejoined = await store.JoinAsync(
            new WaitingRoomJoinCommand(
                dropId,
                firstAdvance.PromotedUserIds[0],
                WaitingRoomJoinMode.Open),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, firstAdvance.PromotedUserIds.Count);
        Assert.Equal(2, secondAdvance.ExpiredUserIds.Count);
        Assert.Equal(2, secondAdvance.PromotedUserIds.Count);
        Assert.All(expiredEntries, entry => Assert.Equal(
            WaitingRoomEntryState.Expired,
            entry!.State));
        Assert.All(promotedEntries, entry => Assert.Equal(
            WaitingRoomEntryState.Admitted,
            entry!.State));
        Assert.Equal(WaitingRoomJoinOutcome.Rejoined, rejoined.Outcome);
        Assert.Equal(5, rejoined.Entry!.Order);
        Assert.Equal(WaitingRoomEntryState.Waiting, rejoined.Entry.State);
    }

    [Fact]
    public async Task JoinAsync_WhileAdmissionIsValid_DoesNotExtendAbsoluteExpiry()
    {
        var dropId = NextDropId();
        var policy = new WaitingRoomPolicyOptions
        {
            ConcurrentAdmissionLimit = 1,
            AdmissionLifetime = TimeSpan.FromSeconds(1),
        };
        var store = _fixture.NewStore(policy);
        await store.JoinAsync(
            new WaitingRoomJoinCommand(dropId, 1, WaitingRoomJoinMode.Open),
            TestContext.Current.CancellationToken);
        await store.FinalizeOrderAsync(
            dropId,
            TestContext.Current.CancellationToken);
        await store.AdvanceAdmissionsAsync(
            dropId,
            TestContext.Current.CancellationToken);
        var before = await store.GetEntryAsync(
            dropId,
            1,
            TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        var repeated = await store.JoinAsync(
            new WaitingRoomJoinCommand(dropId, 1, WaitingRoomJoinMode.Open),
            TestContext.Current.CancellationToken);

        Assert.NotNull(before);
        Assert.Equal(WaitingRoomJoinOutcome.Existing, repeated.Outcome);
        Assert.Equal(WaitingRoomEntryState.Admitted, repeated.Entry!.State);
        Assert.Equal(
            before.AdmissionExpiresUnixTimeMilliseconds,
            repeated.Entry.AdmissionExpiresUnixTimeMilliseconds);
        Assert.True(repeated.Entry.LastSeenUnixTimeMilliseconds > before.LastSeenUnixTimeMilliseconds);
    }

    [Fact]
    public async Task ValidateAdmissionAsync_RequiresMatchingUserDropAndAdmittedState()
    {
        var dropId = NextDropId();
        var otherDropId = NextDropId();
        var store = _fixture.NewStore();
        await store.JoinAsync(
            new WaitingRoomJoinCommand(dropId, 1, WaitingRoomJoinMode.Open),
            TestContext.Current.CancellationToken);
        var waiting = await store.ValidateAdmissionAsync(
            dropId,
            1,
            TestContext.Current.CancellationToken);
        await store.FinalizeOrderAsync(
            dropId,
            TestContext.Current.CancellationToken);
        await store.AdvanceAdmissionsAsync(
            dropId,
            TestContext.Current.CancellationToken);

        var valid = await store.ValidateAdmissionAsync(
            dropId,
            1,
            TestContext.Current.CancellationToken);
        var otherUser = await store.ValidateAdmissionAsync(
            dropId,
            2,
            TestContext.Current.CancellationToken);
        var otherDrop = await store.ValidateAdmissionAsync(
            otherDropId,
            1,
            TestContext.Current.CancellationToken);

        Assert.Equal(WaitingRoomAdmissionValidationOutcome.NotAdmitted, waiting.Outcome);
        Assert.Equal(WaitingRoomAdmissionValidationOutcome.Valid, valid.Outcome);
        Assert.Equal(WaitingRoomEntryState.Admitted, valid.Entry!.State);
        Assert.Equal(WaitingRoomAdmissionValidationOutcome.Missing, otherUser.Outcome);
        Assert.Equal(WaitingRoomAdmissionValidationOutcome.Missing, otherDrop.Outcome);
    }

    [Fact]
    public async Task ValidateAdmissionAsync_AfterAbsoluteExpiry_AtomicallyMarksEntryExpired()
    {
        var dropId = NextDropId();
        var store = _fixture.NewStore(new WaitingRoomPolicyOptions
        {
            ConcurrentAdmissionLimit = 1,
            AdmissionLifetime = TimeSpan.FromMilliseconds(100),
        });
        await store.JoinAsync(
            new WaitingRoomJoinCommand(dropId, 1, WaitingRoomJoinMode.Open),
            TestContext.Current.CancellationToken);
        await store.FinalizeOrderAsync(
            dropId,
            TestContext.Current.CancellationToken);
        await store.AdvanceAdmissionsAsync(
            dropId,
            TestContext.Current.CancellationToken);
        await Task.Delay(150, TestContext.Current.CancellationToken);

        var result = await store.ValidateAdmissionAsync(
            dropId,
            1,
            TestContext.Current.CancellationToken);
        var snapshot = await store.GetSnapshotAsync(
            dropId,
            1,
            TestContext.Current.CancellationToken);

        Assert.Equal(WaitingRoomAdmissionValidationOutcome.Expired, result.Outcome);
        Assert.Equal(WaitingRoomEntryState.Expired, result.Entry!.State);
        Assert.Equal(WaitingRoomEntryState.Expired, snapshot!.Entry.State);
        Assert.Equal(0, snapshot.AdmittedCount);
    }

    [Fact]
    public async Task JoinAsync_RefreshesActiveStateTtlAndHealthCheckUsesRealRedis()
    {
        var dropId = NextDropId();
        await _fixture.NewStore().JoinAsync(
            new WaitingRoomJoinCommand(dropId, 88, WaitingRoomJoinMode.Open),
            TestContext.Current.CancellationToken);

        var database = await _fixture.GetDatabaseAsync();
        var ttl = await database.KeyTimeToLiveAsync(
            _fixture.GetKeys().Entries(dropId));
        var health = await new WaitingRoomRedisHealthCheck(_fixture.GetConnection())
            .CheckHealthAsync(
                new HealthCheckContext(),
                TestContext.Current.CancellationToken);

        Assert.NotNull(ttl);
        Assert.InRange(
            ttl.Value,
            TimeSpan.FromDays(6.9),
            TimeSpan.FromDays(7));
        Assert.Equal(HealthStatus.Healthy, health.Status);
    }

    private static long NextDropId() => Interlocked.Increment(ref _nextDropId);
}
