using System.Diagnostics;
using IndieFi.Web.Modules.KeyDistributions.Application;
using IndieFi.Web.Modules.KeyDistributions.Domain;
using IndieFi.Web.Modules.KeyDistributions.Infrastructure;
using Microsoft.Extensions.Options;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(RedisCollection.Name)]
public sealed class WaitingRoomMultiInstanceLoadTests
{
    private const int InstanceCount = 4;
    private const int UserCount = 500;
    private readonly RedisFixture _fixture;

    public WaitingRoomMultiInstanceLoadTests(RedisFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task FourIndependentInstances_HandleDuplicateOpenBurstWithoutOrderOrLimitDrift()
    {
        var dropId = Random.Shared.NextInt64(1, long.MaxValue);
        var policy = Options.Create(new WaitingRoomPolicyOptions());
        var redisOptions = Options.Create(new WaitingRoomRedisOptions
        {
            Enabled = true,
            ConnectionString = _fixture.ConnectionString,
            KeyPrefix = "test:waiting-room:v1",
            ConnectTimeout = TimeSpan.FromSeconds(5),
        });
        var keys = new WaitingRoomRedisKeys(redisOptions);
        var connections = Enumerable.Range(0, InstanceCount)
            .Select(_ => new WaitingRoomRedisConnection(redisOptions))
            .ToArray();
        var disposed = new bool[InstanceCount];
        try
        {
            var stores = connections
                .Select(connection => (IWaitingRoomStore)new RedisWaitingRoomStore(
                    connection,
                    keys,
                    policy))
                .ToArray();
            await Task.WhenAll(connections.Select(connection => connection.GetAsync()));
            var stopwatch = Stopwatch.StartNew();

            var joins = await Task.WhenAll(Enumerable.Range(1, UserCount).SelectMany(userId =>
                Enumerable.Range(0, 2).Select(attempt => stores[(userId + attempt) % InstanceCount]
                    .JoinAsync(
                        new WaitingRoomJoinCommand(
                            dropId,
                            userId,
                            WaitingRoomJoinMode.Open),
                        TestContext.Current.CancellationToken))));
            await Task.WhenAll(stores.Select(store => store.FinalizeOrderAsync(
                dropId,
                TestContext.Current.CancellationToken)));

            await connections[1].DisposeAsync();
            disposed[1] = true;
            await connections[3].DisposeAsync();
            disposed[3] = true;
            var survivingStores = new[] { stores[0], stores[2] };
            var advances = await Task.WhenAll(Enumerable.Range(0, 50).Select(index =>
                survivingStores[index % survivingStores.Length].AdvanceAdmissionsAsync(
                    dropId,
                    TestContext.Current.CancellationToken)));
            var entries = await Task.WhenAll(Enumerable.Range(1, UserCount).Select(userId =>
                survivingStores[userId % survivingStores.Length].GetEntryAsync(
                    dropId,
                    userId,
                    TestContext.Current.CancellationToken)));
            stopwatch.Stop();

            Assert.Equal(UserCount, joins.Count(result =>
                result.Outcome == WaitingRoomJoinOutcome.Created));
            Assert.Equal(UserCount, joins.Count(result =>
                result.Outcome == WaitingRoomJoinOutcome.Existing));
            Assert.All(entries, Assert.NotNull);
            Assert.Equal(
                Enumerable.Range(1, UserCount).Select(value => (long)value),
                entries.Select(entry => entry!.Order).Order());
            Assert.Equal(10, entries.Count(entry =>
                entry!.State == WaitingRoomEntryState.Admitted));
            Assert.Equal(UserCount - 10, entries.Count(entry =>
                entry!.State == WaitingRoomEntryState.Waiting));
            Assert.Equal(10, advances.Sum(result => result.PromotedUserIds.Count));
            Assert.All(advances, result => Assert.Equal(10, result.AdmittedCount));
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(30),
                $"The 500-user bootstrap envelope took {stopwatch.Elapsed}.");
        }
        finally
        {
            for (var index = 0; index < connections.Length; index++)
            {
                if (!disposed[index])
                {
                    await connections[index].DisposeAsync();
                }
            }
        }
    }
}
