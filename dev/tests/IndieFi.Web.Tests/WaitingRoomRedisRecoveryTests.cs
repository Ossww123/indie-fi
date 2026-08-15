using IndieFi.Web.Modules.KeyDistributions.Application;
using IndieFi.Web.Modules.KeyDistributions.Domain;
using IndieFi.Web.Modules.KeyDistributions.Infrastructure;
using Microsoft.Extensions.Options;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(RedisCollection.Name)]
public sealed class WaitingRoomRedisRecoveryTests
{
    private readonly RedisFixture _fixture;

    public WaitingRoomRedisRecoveryTests(RedisFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AofRestart_ClosesWritesDuringOutageAndRestoresExactWaitingState()
    {
        const int UserCount = 40;
        var dropId = Random.Shared.NextInt64(1, long.MaxValue);
        var policy = new WaitingRoomPolicyOptions
        {
            ConcurrentAdmissionLimit = 3,
            AdmissionLifetime = TimeSpan.FromMinutes(5),
        };
        var store = _fixture.NewStore(policy);
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
        await store.AdvanceAdmissionsAsync(
            dropId,
            TestContext.Current.CancellationToken);
        var before = await ReadEntriesAsync(store, dropId, UserCount);

        await _fixture.StopAsync();
        try
        {
            var unavailableOptions = _fixture.CreateOptions();
            unavailableOptions.ConnectTimeout = TimeSpan.FromMilliseconds(100);
            await using var unavailableConnection = new WaitingRoomRedisConnection(
                Options.Create(unavailableOptions));
            var unavailableStore = new RedisWaitingRoomStore(
                unavailableConnection,
                new WaitingRoomRedisKeys(Options.Create(unavailableOptions)),
                Options.Create(policy));

            await Assert.ThrowsAsync<WaitingRoomUnavailableException>(() =>
                unavailableStore.JoinAsync(
                    new WaitingRoomJoinCommand(
                        dropId,
                        UserCount + 1,
                        WaitingRoomJoinMode.Open),
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            await _fixture.StartAsync();
        }

        var recoveredStore = _fixture.NewStore(policy);
        var after = await ReadEntriesAsync(recoveredStore, dropId, UserCount);
        var admittedUserId = after.First(item =>
            item.Value.State == WaitingRoomEntryState.Admitted).Key;
        var admissionExpiry = after[admittedUserId]
            .AdmissionExpiresUnixTimeMilliseconds;
        var repeated = await recoveredStore.JoinAsync(
            new WaitingRoomJoinCommand(
                dropId,
                admittedUserId,
                WaitingRoomJoinMode.Open),
            TestContext.Current.CancellationToken);

        Assert.Equal(before, after);
        Assert.Equal(3, after.Count(item =>
            item.Value.State == WaitingRoomEntryState.Admitted));
        Assert.Equal(UserCount - 3, after.Count(item =>
            item.Value.State == WaitingRoomEntryState.Waiting));
        Assert.Equal(WaitingRoomJoinOutcome.Existing, repeated.Outcome);
        Assert.Equal(
            admissionExpiry,
            repeated.Entry!.AdmissionExpiresUnixTimeMilliseconds);
    }

    private static async Task<Dictionary<long, WaitingRoomStoredEntry>> ReadEntriesAsync(
        IWaitingRoomStore store,
        long dropId,
        int userCount)
    {
        var entries = await Task.WhenAll(Enumerable.Range(1, userCount).Select(async userId =>
            new KeyValuePair<long, WaitingRoomStoredEntry>(
                userId,
                (await store.GetEntryAsync(
                    dropId,
                    userId,
                    TestContext.Current.CancellationToken))!)));
        return entries.ToDictionary();
    }
}
