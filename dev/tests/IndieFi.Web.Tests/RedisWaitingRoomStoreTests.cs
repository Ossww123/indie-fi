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
