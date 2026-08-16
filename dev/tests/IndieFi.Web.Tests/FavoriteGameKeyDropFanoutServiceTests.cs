using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Contracts;
using IndieFi.Web.Modules.Notifications.Domain;
using IndieFi.Web.Modules.Profiles.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class FavoriteGameKeyDropFanoutServiceTests
{
    private readonly PostgreSqlFixture _fixture;

    public FavoriteGameKeyDropFanoutServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task StageBatch_UsesActiveFavoritesAndExcludesOwner()
    {
        long ownerId;
        long firstRecipientId;
        long secondRecipientId;
        long suspendedRecipientId;
        long dropId;
        DateTimeOffset occurredAt;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            ownerId = (await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, 0)).Id;
            firstRecipientId = (await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, 0)).Id;
            secondRecipientId = (await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, 0)).Id;
            suspendedRecipientId = (await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, 0)).Id;
            var game = await ReviewTestData.CreateGameAsync(arrangeDb, ownerId);
            var drop = new KeyDrop
            {
                GameId = game.Id,
                OwnerId = ownerId,
                OpensAt = DateTimeOffset.UtcNow.AddHours(1),
                TotalKeys = 1,
            };
            arrangeDb.KeyDrops.Add(drop);
            arrangeDb.FavoriteGames.AddRange(
                Favorite(ownerId, game.Id),
                Favorite(firstRecipientId, game.Id),
                Favorite(secondRecipientId, game.Id),
                Favorite(suspendedRecipientId, game.Id));
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            var transitions = new AccountStatusTransitionService(
                arrangeDb,
                TimeProvider.System);
            Assert.Equal(
                AccountStatusTransitionOutcome.Changed,
                (await transitions.SuspendIndefinitelyAsync(
                    suspendedRecipientId,
                    TestContext.Current.CancellationToken)).Outcome);
            dropId = drop.Id;
            occurredAt = drop.CreatedAt;
        }

        var publisher = new TestNotificationEventPublisher();
        var fanoutId = Guid.NewGuid();
        await using var db = _fixture.NewDbContext();
        var result = await new FavoriteGameKeyDropFanoutService(
            db,
            publisher).StageBatchAsync(
                new FavoriteGameKeyDropCreatedFanoutV1(
                    fanoutId,
                    dropId,
                    AfterUserId: 0,
                    occurredAt),
                TestContext.Current.CancellationToken);

        Assert.Equal(2, result.StagedCount);
        Assert.Null(result.NextAfterUserId);
        Assert.Equal(
            [firstRecipientId, secondRecipientId],
            publisher.Events.Select(item => item.RecipientUserId));
        Assert.All(publisher.Events, staged =>
        {
            Assert.Equal(NotificationKind.FavoriteGameKeyDropCreated, staged.Kind);
            Assert.Equal(NotificationSourceTypes.KeyDrop, staged.SourceType);
            Assert.Equal(dropId, staged.SourceId);
            Assert.Equal(occurredAt, staged.OccurredAt);
            Assert.Equal(
                NotificationEventId.Create(
                    NotificationKind.FavoriteGameKeyDropCreated,
                    dropId,
                    staged.RecipientUserId),
                staged.EventId);
        });
    }

    [Fact]
    public async Task StageBatch_AfterCursorDoesNotRepeatEarlierRecipient()
    {
        long firstRecipientId;
        long secondRecipientId;
        long dropId;
        DateTimeOffset occurredAt;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var owner = await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, 0);
            firstRecipientId = (await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, 0)).Id;
            secondRecipientId = (await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, 0)).Id;
            var game = await ReviewTestData.CreateGameAsync(arrangeDb, owner.Id);
            var drop = new KeyDrop
            {
                GameId = game.Id,
                OwnerId = owner.Id,
                OpensAt = DateTimeOffset.UtcNow.AddHours(1),
                TotalKeys = 1,
            };
            arrangeDb.KeyDrops.Add(drop);
            arrangeDb.FavoriteGames.AddRange(
                Favorite(firstRecipientId, game.Id),
                Favorite(secondRecipientId, game.Id));
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            dropId = drop.Id;
            occurredAt = drop.CreatedAt;
        }

        var publisher = new TestNotificationEventPublisher();
        await using var db = _fixture.NewDbContext();
        var result = await new FavoriteGameKeyDropFanoutService(
            db,
            publisher).StageBatchAsync(
                new FavoriteGameKeyDropCreatedFanoutV1(
                    Guid.NewGuid(),
                    dropId,
                    firstRecipientId,
                    occurredAt),
                TestContext.Current.CancellationToken);

        Assert.Equal(1, result.StagedCount);
        Assert.Equal(secondRecipientId, Assert.Single(publisher.Events).RecipientUserId);
    }

    [Fact]
    public async Task StageBatch_MoreThanBatchSize_ContinuesWithoutLossOrOverlap()
    {
        long dropId;
        DateTimeOffset occurredAt;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var owner = await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, 0);
            var game = await ReviewTestData.CreateGameAsync(arrangeDb, owner.Id);
            var drop = new KeyDrop
            {
                GameId = game.Id,
                OwnerId = owner.Id,
                OpensAt = DateTimeOffset.UtcNow.AddHours(1),
                TotalKeys = 1,
            };
            arrangeDb.KeyDrops.Add(drop);
            var recipients = Enumerable
                .Range(0, FavoriteGameKeyDropFanoutService.BatchSize + 1)
                .Select(_ => TestAccountProtection.AddUser(
                    arrangeDb,
                    $"{Guid.NewGuid():N}@example.test"))
                .ToArray();
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            arrangeDb.FavoriteGames.AddRange(
                recipients.Select(user => Favorite(user.Id, game.Id)));
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            dropId = drop.Id;
            occurredAt = drop.CreatedAt;
        }

        var publisher = new TestNotificationEventPublisher();
        var fanoutId = Guid.NewGuid();
        await using var db = _fixture.NewDbContext();
        var service = new FavoriteGameKeyDropFanoutService(db, publisher);
        var first = await service.StageBatchAsync(
            new FavoriteGameKeyDropCreatedFanoutV1(
                fanoutId,
                dropId,
                AfterUserId: 0,
                occurredAt),
            TestContext.Current.CancellationToken);
        var second = await service.StageBatchAsync(
            new FavoriteGameKeyDropCreatedFanoutV1(
                fanoutId,
                dropId,
                first.NextAfterUserId!.Value,
                occurredAt),
            TestContext.Current.CancellationToken);

        Assert.Equal(FavoriteGameKeyDropFanoutService.BatchSize, first.StagedCount);
        Assert.Equal(1, second.StagedCount);
        Assert.Null(second.NextAfterUserId);
        Assert.Equal(
            FavoriteGameKeyDropFanoutService.BatchSize + 1,
            publisher.Events.Select(item => item.RecipientUserId).Distinct().Count());
    }

    private static FavoriteGame Favorite(long userId, long gameId) => new()
    {
        UserId = userId,
        GameId = gameId,
    };
}
