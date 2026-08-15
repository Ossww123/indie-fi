using IndieFi.Web.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class KeyDropStorageSchemaTests
{
    private readonly PostgreSqlFixture _fixture;

    public KeyDropStorageSchemaTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Database_RejectsNegativeTotalKeyCount()
    {
        await using var db = _fixture.NewDbContext();
        var owner = TestAccountProtection.AddUser(
            db,
            $"{Guid.NewGuid():N}@example.test");
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var game = new Game
        {
            OwnerId = owner.Id,
            Title = $"Key drop schema game {Guid.NewGuid():N}",
        };
        db.Games.Add(game);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.KeyDrops.Add(new KeyDrop
        {
            GameId = game.Id,
            OwnerId = owner.Id,
            OpensAt = DateTimeOffset.UtcNow.AddHours(1),
            TotalKeys = -1,
        });

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
            db.SaveChangesAsync(TestContext.Current.CancellationToken));
        var postgresException = Assert.IsType<PostgresException>(
            exception.InnerException);

        Assert.Equal(PostgresErrorCodes.CheckViolation, postgresException.SqlState);
        Assert.Equal(
            "CK_key_drops_total_keys_nonnegative",
            postgresException.ConstraintName);
    }
}
