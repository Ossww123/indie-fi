using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Contracts;
using IndieFi.Web.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class SubscribedKeyDropOpenedFanoutServiceTests
{
    private readonly PostgreSqlFixture _fixture;

    public SubscribedKeyDropOpenedFanoutServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task StageBatch_UsesActiveSubscribersAndExcludesOwner()
    {
        long ownerId;
        long activeSubscriberId;
        long suspendedSubscriberId;
        long dropId;
        DateTimeOffset opensAt;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            ownerId = (await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, 0)).Id;
            activeSubscriberId = (await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, 0)).Id;
            suspendedSubscriberId = (await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, 0)).Id;
            var game = await ReviewTestData.CreateGameAsync(arrangeDb, ownerId);
            var drop = new KeyDrop
            {
                GameId = game.Id,
                OwnerId = ownerId,
                OpensAt = DateTimeOffset.UtcNow,
                TotalKeys = 1,
            };
            arrangeDb.KeyDrops.Add(drop);
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            arrangeDb.KeyDropSubscriptions.AddRange(
                Subscription(ownerId, drop.Id),
                Subscription(activeSubscriberId, drop.Id),
                Subscription(suspendedSubscriberId, drop.Id));
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            var transitions = new AccountStatusTransitionService(
                arrangeDb,
                TimeProvider.System);
            Assert.Equal(
                AccountStatusTransitionOutcome.Changed,
                (await transitions.SuspendIndefinitelyAsync(
                    suspendedSubscriberId,
                    TestContext.Current.CancellationToken)).Outcome);
            dropId = drop.Id;
            opensAt = drop.OpensAt;
        }

        var publisher = new TestNotificationEventPublisher();
        await using var db = _fixture.NewDbContext();
        var result = await new SubscribedKeyDropOpenedFanoutService(
            db,
            publisher).StageBatchAsync(
                new SubscribedKeyDropOpenedFanoutV1(
                    Guid.NewGuid(),
                    dropId,
                    AfterUserId: 0,
                    opensAt),
                TestContext.Current.CancellationToken);

        Assert.Equal(1, result.StagedCount);
        var staged = Assert.Single(publisher.Events);
        Assert.Equal(activeSubscriberId, staged.RecipientUserId);
        Assert.Equal(NotificationKind.KeyDropOpened, staged.Kind);
        Assert.Equal(NotificationSourceTypes.KeyDrop, staged.SourceType);
        Assert.Equal(dropId, staged.SourceId);
        Assert.Equal(opensAt, staged.OccurredAt);
        Assert.Equal(
            NotificationEventId.Create(
                NotificationKind.KeyDropOpened,
                dropId,
                activeSubscriberId),
            staged.EventId);
    }

    [Fact]
    public async Task StageBatch_RemovedSubscriptionIsNotStaged()
    {
        long userId;
        long dropId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var owner = await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, 0);
            userId = (await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, 0)).Id;
            var game = await ReviewTestData.CreateGameAsync(arrangeDb, owner.Id);
            var drop = new KeyDrop
            {
                GameId = game.Id,
                OwnerId = owner.Id,
                OpensAt = DateTimeOffset.UtcNow,
                TotalKeys = 1,
            };
            arrangeDb.KeyDrops.Add(drop);
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            var subscription = Subscription(userId, drop.Id);
            arrangeDb.KeyDropSubscriptions.Add(subscription);
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            arrangeDb.KeyDropSubscriptions.Remove(subscription);
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            dropId = drop.Id;
        }

        var publisher = new TestNotificationEventPublisher();
        await using var db = _fixture.NewDbContext();
        var result = await new SubscribedKeyDropOpenedFanoutService(
            db,
            publisher).StageBatchAsync(
                new SubscribedKeyDropOpenedFanoutV1(
                    Guid.NewGuid(),
                    dropId,
                    AfterUserId: 0,
                    DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken);

        Assert.Equal(0, result.StagedCount);
        Assert.Empty(publisher.Events);
    }

    private static IndieFi.Web.Modules.Notifications.Domain.KeyDropSubscription Subscription(
        long userId,
        long dropId) => new()
    {
        UserId = userId,
        DropId = dropId,
    };
}
