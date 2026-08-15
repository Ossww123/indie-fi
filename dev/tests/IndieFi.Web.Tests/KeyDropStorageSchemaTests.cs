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

    [Fact]
    public async Task Database_RejectsNegativeStateVersion()
    {
        await using var db = _fixture.NewDbContext();
        var owner = TestAccountProtection.AddUser(
            db,
            $"{Guid.NewGuid():N}@example.test");
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var game = new Game
        {
            OwnerId = owner.Id,
            Title = $"Key drop version game {Guid.NewGuid():N}",
        };
        db.Games.Add(game);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.KeyDrops.Add(new KeyDrop
        {
            GameId = game.Id,
            OwnerId = owner.Id,
            OpensAt = DateTimeOffset.UtcNow.AddHours(1),
            TotalKeys = 1,
            StateVersion = -1,
        });

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
            db.SaveChangesAsync(TestContext.Current.CancellationToken));
        var postgresException = Assert.IsType<PostgresException>(
            exception.InnerException);

        Assert.Equal(PostgresErrorCodes.CheckViolation, postgresException.SqlState);
        Assert.Equal(
            "CK_key_drops_state_version_nonnegative",
            postgresException.ConstraintName);
    }

    [Theory]
    [InlineData(0, "CK_key_drops_minimum_account_age_days_range")]
    [InlineData(366, "CK_key_drops_minimum_account_age_days_range")]
    public async Task Database_RejectsAccountAgeOutsidePolicyRange(
        int minimumAccountAgeDays,
        string expectedConstraint)
    {
        var exception = await InsertDropExpectingFailureAsync(
            minimumAccountAgeDays,
            minimumCompletedReviewCount: null);

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(expectedConstraint, exception.ConstraintName);
    }

    [Theory]
    [InlineData(0, "CK_key_drops_minimum_completed_review_count_range")]
    [InlineData(101, "CK_key_drops_minimum_completed_review_count_range")]
    public async Task Database_RejectsReviewCountOutsidePolicyRange(
        int minimumCompletedReviewCount,
        string expectedConstraint)
    {
        var exception = await InsertDropExpectingFailureAsync(
            minimumAccountAgeDays: null,
            minimumCompletedReviewCount);

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(expectedConstraint, exception.ConstraintName);
    }

    [Fact]
    public async Task Database_AllowsExistingUnrestrictedDropShape()
    {
        await using var db = _fixture.NewDbContext();
        var owner = TestAccountProtection.AddUser(
            db,
            $"{Guid.NewGuid():N}@example.test");
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var game = new Game
        {
            OwnerId = owner.Id,
            Title = $"Unrestricted drop game {Guid.NewGuid():N}",
        };
        db.Games.Add(game);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var drop = new KeyDrop
        {
            GameId = game.Id,
            OwnerId = owner.Id,
            OpensAt = DateTimeOffset.UtcNow.AddHours(1),
            TotalKeys = 1,
        };
        db.KeyDrops.Add(drop);

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Null(drop.MinimumAccountAgeDays);
        Assert.Null(drop.MinimumCompletedReviewCount);
    }

    private async Task<PostgresException> InsertDropExpectingFailureAsync(
        int? minimumAccountAgeDays,
        int? minimumCompletedReviewCount)
    {
        await using var db = _fixture.NewDbContext();
        var owner = TestAccountProtection.AddUser(
            db,
            $"{Guid.NewGuid():N}@example.test");
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var game = new Game
        {
            OwnerId = owner.Id,
            Title = $"Eligibility schema game {Guid.NewGuid():N}",
        };
        db.Games.Add(game);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.KeyDrops.Add(new KeyDrop
        {
            GameId = game.Id,
            OwnerId = owner.Id,
            OpensAt = DateTimeOffset.UtcNow.AddHours(1),
            TotalKeys = 1,
            MinimumAccountAgeDays = minimumAccountAgeDays,
            MinimumCompletedReviewCount = minimumCompletedReviewCount,
        });

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
            db.SaveChangesAsync(TestContext.Current.CancellationToken));
        return Assert.IsType<PostgresException>(exception.InnerException);
    }
}
