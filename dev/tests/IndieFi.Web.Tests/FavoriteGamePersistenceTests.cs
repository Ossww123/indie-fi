using IndieFi.Web.Modules.Profiles.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class FavoriteGamePersistenceTests
{
    private readonly PostgreSqlFixture _fixture;

    public FavoriteGamePersistenceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Save_ValidFavorite_PersistsRelationshipAndCreatedTime()
    {
        var (userId, gameId) = await CreateUserAndGameAsync();
        await using (var db = _fixture.NewDbContext())
        {
            db.FavoriteGames.Add(new FavoriteGame
            {
                UserId = userId,
                GameId = gameId,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var favorite = await verificationDb.FavoriteGames.SingleAsync(
            item => item.UserId == userId && item.GameId == gameId,
            TestContext.Current.CancellationToken);
        Assert.NotEqual(default, favorite.CreatedAt);
    }

    [Fact]
    public async Task Save_DuplicateFavorite_IsRejectedByCompositePrimaryKey()
    {
        var (userId, gameId) = await CreateUserAndGameAsync();
        await using (var db = _fixture.NewDbContext())
        {
            db.FavoriteGames.Add(new FavoriteGame
            {
                UserId = userId,
                GameId = gameId,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var duplicateDb = _fixture.NewDbContext();
        duplicateDb.FavoriteGames.Add(new FavoriteGame
        {
            UserId = userId,
            GameId = gameId,
        });

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => duplicateDb.SaveChangesAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            PostgresErrorCodes.UniqueViolation,
            Assert.IsType<PostgresException>(exception.InnerException).SqlState);
    }

    [Fact]
    public async Task DeleteGame_CascadesFavoriteRelationships()
    {
        var (userId, gameId) = await CreateUserAndGameAsync();
        await using (var db = _fixture.NewDbContext())
        {
            db.FavoriteGames.Add(new FavoriteGame
            {
                UserId = userId,
                GameId = gameId,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var ownerDb = _fixture.NewOwnerDbContext())
        {
            await ownerDb.Games
                .Where(game => game.Id == gameId)
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        Assert.False(await verificationDb.FavoriteGames.AnyAsync(
            item => item.UserId == userId && item.GameId == gameId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ApplicationRole_CannotRewriteFavoriteHistory()
    {
        var (userId, gameId) = await CreateUserAndGameAsync();
        await using var db = _fixture.NewDbContext();
        db.FavoriteGames.Add(new FavoriteGame
        {
            UserId = userId,
            GameId = gameId,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => db.FavoriteGames
                .Where(item => item.UserId == userId && item.GameId == gameId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        item => item.CreatedAt,
                        DateTimeOffset.UtcNow.AddDays(-1)),
                    TestContext.Current.CancellationToken));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
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
