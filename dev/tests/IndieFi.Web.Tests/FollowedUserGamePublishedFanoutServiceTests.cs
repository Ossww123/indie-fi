using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Contracts;
using IndieFi.Web.Modules.Notifications.Domain;
using IndieFi.Web.Modules.Profiles.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class FollowedUserGamePublishedFanoutServiceTests
{
    private readonly PostgreSqlFixture _fixture;

    public FollowedUserGamePublishedFanoutServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task StageBatch_UsesCurrentActiveFollowers()
    {
        long ownerId;
        long activeFollowerId;
        long suspendedFollowerId;
        long unrelatedUserId;
        long gameId;
        DateTimeOffset occurredAt;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            ownerId = (await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, 0)).Id;
            activeFollowerId = (await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, 0)).Id;
            suspendedFollowerId = (await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, 0)).Id;
            unrelatedUserId = (await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, 0)).Id;
            var game = await ReviewTestData.CreateGameAsync(arrangeDb, ownerId);
            arrangeDb.UserFollows.AddRange(
                Follow(activeFollowerId, ownerId),
                Follow(suspendedFollowerId, ownerId));
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            var transitions = new AccountStatusTransitionService(
                arrangeDb,
                TimeProvider.System);
            Assert.Equal(
                AccountStatusTransitionOutcome.Changed,
                (await transitions.SuspendIndefinitelyAsync(
                    suspendedFollowerId,
                    TestContext.Current.CancellationToken)).Outcome);
            gameId = game.Id;
            occurredAt = game.CreatedAt;
        }

        var publisher = new TestNotificationEventPublisher();
        await using var db = _fixture.NewDbContext();
        var result = await new FollowedUserGamePublishedFanoutService(
            db,
            publisher).StageBatchAsync(
                new FollowedUserGamePublishedFanoutV1(
                    Guid.NewGuid(),
                    gameId,
                    AfterUserId: 0,
                    occurredAt),
                TestContext.Current.CancellationToken);

        Assert.Equal(1, result.StagedCount);
        Assert.Null(result.NextAfterUserId);
        var staged = Assert.Single(publisher.Events);
        Assert.Equal(activeFollowerId, staged.RecipientUserId);
        Assert.NotEqual(unrelatedUserId, staged.RecipientUserId);
        Assert.Equal(NotificationKind.FollowedUserGamePublished, staged.Kind);
        Assert.Equal(NotificationSourceTypes.Game, staged.SourceType);
        Assert.Equal(gameId, staged.SourceId);
        Assert.Equal(occurredAt, staged.OccurredAt);
        Assert.Equal(
            NotificationEventId.Create(
                NotificationKind.FollowedUserGamePublished,
                gameId,
                activeFollowerId),
            staged.EventId);
    }

    [Fact]
    public async Task StageBatch_RemovedFollowIsNotStaged()
    {
        long followerId;
        long gameId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var owner = await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, 0);
            followerId = (await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, 0)).Id;
            var game = await ReviewTestData.CreateGameAsync(arrangeDb, owner.Id);
            var follow = Follow(followerId, owner.Id);
            arrangeDb.UserFollows.Add(follow);
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            arrangeDb.UserFollows.Remove(follow);
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            gameId = game.Id;
        }

        var publisher = new TestNotificationEventPublisher();
        await using var db = _fixture.NewDbContext();
        var result = await new FollowedUserGamePublishedFanoutService(
            db,
            publisher).StageBatchAsync(
                new FollowedUserGamePublishedFanoutV1(
                    Guid.NewGuid(),
                    gameId,
                    AfterUserId: 0,
                    DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken);

        Assert.Equal(0, result.StagedCount);
        Assert.Empty(publisher.Events);
    }

    [Fact]
    public async Task StageBatch_MoreThanBatchSize_ContinuesWithoutLossOrOverlap()
    {
        long gameId;
        DateTimeOffset occurredAt;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var owner = await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, 0);
            var game = await ReviewTestData.CreateGameAsync(arrangeDb, owner.Id);
            var followers = Enumerable
                .Range(0, FollowedUserGamePublishedFanoutService.BatchSize + 1)
                .Select(_ => TestAccountProtection.AddUser(
                    arrangeDb,
                    $"{Guid.NewGuid():N}@example.test"))
                .ToArray();
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            arrangeDb.UserFollows.AddRange(
                followers.Select(user => Follow(user.Id, owner.Id)));
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            gameId = game.Id;
            occurredAt = game.CreatedAt;
        }

        var publisher = new TestNotificationEventPublisher();
        var fanoutId = Guid.NewGuid();
        await using var db = _fixture.NewDbContext();
        var service = new FollowedUserGamePublishedFanoutService(db, publisher);
        var first = await service.StageBatchAsync(
            new FollowedUserGamePublishedFanoutV1(
                fanoutId,
                gameId,
                AfterUserId: 0,
                occurredAt),
            TestContext.Current.CancellationToken);
        var second = await service.StageBatchAsync(
            new FollowedUserGamePublishedFanoutV1(
                fanoutId,
                gameId,
                first.NextAfterUserId!.Value,
                occurredAt),
            TestContext.Current.CancellationToken);

        Assert.Equal(FollowedUserGamePublishedFanoutService.BatchSize, first.StagedCount);
        Assert.Equal(1, second.StagedCount);
        Assert.Null(second.NextAfterUserId);
        Assert.Equal(
            FollowedUserGamePublishedFanoutService.BatchSize + 1,
            publisher.Events.Select(item => item.RecipientUserId).Distinct().Count());
    }

    private static UserFollow Follow(long followerId, long followedUserId) => new()
    {
        FollowerId = followerId,
        FollowedUserId = followedUserId,
    };
}
