using IndieFi.Web.Modules.Accounts.Domain;
using IndieFi.Web.Modules.Profiles.Application;
using IndieFi.Web.Modules.Profiles.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class FavoriteGameServiceTests
{
    private readonly PostgreSqlFixture _fixture;

    public FavoriteGameServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Favorite_ThenRepeat_CreatesOneRelationshipAndReturnsCurrentState()
    {
        var (userId, gameId) = await CreateUserAndGameAsync();
        await using var db = _fixture.NewDbContext();
        var service = new FavoriteGameService(db);

        var first = await service.FavoriteAsync(
            userId,
            gameId,
            TestContext.Current.CancellationToken);
        var repeated = await service.FavoriteAsync(
            userId,
            gameId,
            TestContext.Current.CancellationToken);

        Assert.Equal(FavoriteGameOutcome.Favorited, first.Outcome);
        Assert.True(first.Changed);
        Assert.Equal(FavoriteGameOutcome.Favorited, repeated.Outcome);
        Assert.False(repeated.Changed);
        Assert.True(await service.IsFavoriteAsync(
            userId,
            gameId,
            TestContext.Current.CancellationToken));
        Assert.Equal(1, await db.FavoriteGames.CountAsync(
            item => item.UserId == userId && item.GameId == gameId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Unfavorite_ThenRepeat_RemovesRelationshipIdempotently()
    {
        var (userId, gameId) = await CreateUserAndGameAsync();
        await using var db = _fixture.NewDbContext();
        var service = new FavoriteGameService(db);
        await service.FavoriteAsync(
            userId,
            gameId,
            TestContext.Current.CancellationToken);

        var first = await service.UnfavoriteAsync(
            userId,
            gameId,
            TestContext.Current.CancellationToken);
        var repeated = await service.UnfavoriteAsync(
            userId,
            gameId,
            TestContext.Current.CancellationToken);

        Assert.Equal(FavoriteGameOutcome.NotFavorited, first.Outcome);
        Assert.True(first.Changed);
        Assert.Equal(FavoriteGameOutcome.NotFavorited, repeated.Outcome);
        Assert.False(repeated.Changed);
        Assert.False(await service.IsFavoriteAsync(
            userId,
            gameId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Favorite_MissingGame_ReturnsNotFoundWithoutCreatingRelationship()
    {
        var (userId, _) = await CreateUserAndGameAsync();
        await using var db = _fixture.NewDbContext();

        var result = await new FavoriteGameService(db).FavoriteAsync(
            userId,
            long.MaxValue,
            TestContext.Current.CancellationToken);

        Assert.Equal(FavoriteGameOutcome.GameNotFound, result.Outcome);
        Assert.False(result.Changed);
        Assert.False(await db.FavoriteGames.AnyAsync(
            item => item.UserId == userId,
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(AccountStatus.TemporarilySuspended)]
    [InlineData(AccountStatus.IndefinitelySuspended)]
    public async Task Favorite_UnavailableUser_DoesNotChangeRelationship(
        AccountStatus accountStatus)
    {
        var (userId, gameId) = await CreateUserAndGameAsync();
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var user = await arrangeDb.Users.SingleAsync(
                item => item.Id == userId,
                TestContext.Current.CancellationToken);
            user.AccountStatus = accountStatus;
            user.SuspendedUntil = accountStatus == AccountStatus.TemporarilySuspended
                ? DateTimeOffset.UtcNow.AddHours(1)
                : null;
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var db = _fixture.NewDbContext();
        var result = await new FavoriteGameService(db).FavoriteAsync(
            userId,
            gameId,
            TestContext.Current.CancellationToken);

        Assert.Equal(FavoriteGameOutcome.AccountUnavailable, result.Outcome);
        Assert.False(await db.FavoriteGames.AnyAsync(
            item => item.UserId == userId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Favorite_ConcurrentRepeats_ConvergeToOneRelationship()
    {
        var (userId, gameId) = await CreateUserAndGameAsync();
        await using var firstDb = _fixture.NewDbContext();
        await using var secondDb = _fixture.NewDbContext();

        var results = await Task.WhenAll(
            new FavoriteGameService(firstDb).FavoriteAsync(
                userId,
                gameId,
                TestContext.Current.CancellationToken),
            new FavoriteGameService(secondDb).FavoriteAsync(
                userId,
                gameId,
                TestContext.Current.CancellationToken));

        Assert.All(results, result =>
            Assert.Equal(FavoriteGameOutcome.Favorited, result.Outcome));
        Assert.Single(results, result => result.Changed);
        await using var verificationDb = _fixture.NewDbContext();
        Assert.Equal(1, await verificationDb.FavoriteGames.CountAsync(
            item => item.UserId == userId && item.GameId == gameId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetFavorites_ReturnsNewestFirstWithOwnerDisplayNames()
    {
        long userId;
        long firstGameId;
        long secondGameId;
        long secondOwnerId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            userId = (await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0)).Id;
            var firstOwner = await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0);
            var secondOwner = await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0);
            secondOwnerId = secondOwner.Id;
            var firstGame = await ReviewTestData.CreateGameAsync(arrangeDb, firstOwner.Id);
            firstGame.Title = "First Favorite";
            firstGame.Description = "관심 게임 소개";
            firstGameId = firstGame.Id;
            var secondGame = await ReviewTestData.CreateGameAsync(arrangeDb, secondOwner.Id);
            secondGame.Title = "Second Favorite";
            secondGameId = secondGame.Id;
            var changedAt = DateTimeOffset.UtcNow.AddDays(-8);
            arrangeDb.UserProfiles.Add(new UserProfile
            {
                UserId = firstOwner.Id,
                Nickname = "FavoriteOwner",
                NormalizedNickname = "FAVORITEOWNER",
                NicknameChangedAt = changedAt,
                CreatedAt = changedAt,
                UpdatedAt = changedAt,
            });
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var db = _fixture.NewDbContext();
        var service = new FavoriteGameService(db);
        await service.FavoriteAsync(
            userId,
            firstGameId,
            TestContext.Current.CancellationToken);
        await service.FavoriteAsync(
            userId,
            secondGameId,
            TestContext.Current.CancellationToken);

        var items = await service.GetFavoritesAsync(
            userId,
            TestContext.Current.CancellationToken);

        Assert.Equal([secondGameId, firstGameId], items.Select(item => item.GameId));
        Assert.Equal($"user#{secondOwnerId}", items[0].OwnerDisplayName);
        Assert.Equal("FavoriteOwner", items[1].OwnerDisplayName);
        Assert.Equal("관심 게임 소개", items[1].Description);
    }

    private async Task<(long UserId, long GameId)> CreateUserAndGameAsync()
    {
        await using var db = _fixture.NewDbContext();
        var user = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var owner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var game = await ReviewTestData.CreateGameAsync(db, owner.Id);
        return (user.Id, game.Id);
    }
}
