using IndieFi.Web.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class GameKeyStorageSchemaTests
{
    private readonly PostgreSqlFixture _fixture;

    public GameKeyStorageSchemaTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Database_RejectsGrantWhoseGameKeyBelongsToAnotherDrop()
    {
        await using var db = _fixture.NewDbContext();
        var owner = TestAccountProtection.AddUser(
            db,
            $"{Guid.NewGuid():N}@example.test");
        var recipient = TestAccountProtection.AddUser(
            db,
            $"{Guid.NewGuid():N}@example.test");
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var game = new Game
        {
            OwnerId = owner.Id,
            Title = $"Game {Guid.NewGuid():N}",
        };
        db.Games.Add(game);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var firstDrop = NewDrop(game, owner);
        var secondDrop = NewDrop(game, owner);
        db.KeyDrops.AddRange(firstDrop, secondDrop);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var gameKey = new GameKey
        {
            DropId = secondDrop.Id,
            KeyValue = $"KEY-{Guid.NewGuid():N}",
        };
        db.GameKeys.Add(gameKey);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.KeyGrants.Add(new KeyGrant
        {
            DropId = firstDrop.Id,
            UserId = recipient.Id,
            KeyId = gameKey.Id,
        });

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
            db.SaveChangesAsync(TestContext.Current.CancellationToken));
        var postgresException = Assert.IsType<PostgresException>(
            exception.InnerException);

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, postgresException.SqlState);
        Assert.Equal(
            "FK_key_grants_game_keys_key_id_drop_id",
            postgresException.ConstraintName);
    }

    private static KeyDrop NewDrop(Game game, User owner) => new()
    {
        GameId = game.Id,
        OwnerId = owner.Id,
        OpensAt = DateTimeOffset.UtcNow.AddHours(1),
        TotalKeys = 1,
    };
}
