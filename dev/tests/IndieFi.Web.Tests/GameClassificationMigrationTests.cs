using IndieFi.Web.Data;
using IndieFi.Web.Modules.Games.Application;
using IndieFi.Web.Modules.Games.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class GameClassificationMigrationTests
{
    private const string PreviousMigration =
        "20260816134500_AddNotificationMaintenanceFunction";

    private readonly PostgreSqlFixture _fixture;

    public GameClassificationMigrationTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ExistingGame_IsSafelyMigratedAndCatalogBoundaryWorksForAppRole()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N");
        var database = $"game_classification_{suffix}";
        var databaseCreated = false;

        try
        {
            await ExecuteAdminCommandAsync(
                $"CREATE DATABASE \"{database}\";",
                cancellationToken);
            databaseCreated = true;

            var gameId = await MigrateExistingGameAsync(
                database,
                suffix,
                cancellationToken);
            await VerifyApplicationBoundaryAsync(database, cancellationToken);
            await VerifyMigratedGameAsync(database, gameId, cancellationToken);
        }
        finally
        {
            if (databaseCreated)
            {
                await ExecuteAdminCommandAsync(
                    $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE);",
                    CancellationToken.None);
            }
        }
    }

    private async Task<long> MigrateExistingGameAsync(
        string database,
        string suffix,
        CancellationToken cancellationToken)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.CreateOwnerConnectionString(database))
            .Options;
        await using var db = new AppDbContext(options);
        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration, cancellationToken);

        var owner = TestAccountProtection.AddUser(
            db,
            $"{suffix}@example.test");
        await db.SaveChangesAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var insertGame = new NpgsqlCommand(
            """
            INSERT INTO games (owner_id, title)
            VALUES ($1, $2)
            RETURNING id
            """,
            (NpgsqlConnection)db.Database.GetDbConnection());
        insertGame.Parameters.AddWithValue(owner.Id);
        insertGame.Parameters.AddWithValue($"Legacy game {suffix}");
        var gameId = (long)(await insertGame.ExecuteScalarAsync(cancellationToken))!;

        await migrator.MigrateAsync(cancellationToken: cancellationToken);
        Assert.Contains(
            "20260816150000_AddGameClassifications",
            await db.Database.GetAppliedMigrationsAsync(cancellationToken));
        Assert.Contains(
            "20260816151000_HardenGameClassifications",
            await db.Database.GetAppliedMigrationsAsync(cancellationToken));
        return gameId;
    }

    private async Task VerifyApplicationBoundaryAsync(
        string database,
        CancellationToken cancellationToken)
    {
        await using var db = _fixture.NewApplicationDbContext(database);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var catalog = await new GameClassificationCatalogQueryService(db, cache)
            .GetActiveAsync(cancellationToken);

        Assert.Equal(20, catalog.Genres.Count);
        Assert.Equal(28, catalog.Tags.Count);
        Assert.Equal(12, catalog.Platforms.Count);

        var permissionException = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync(
                "UPDATE game_genres SET display_name = '변조' WHERE id = 10",
                cancellationToken));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, permissionException.SqlState);
    }

    private async Task VerifyMigratedGameAsync(
        string database,
        long gameId,
        CancellationToken cancellationToken)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.CreateOwnerConnectionString(database))
            .Options;
        await using var db = new AppDbContext(options);

        var genreId = await db.Games
            .Where(game => game.Id == gameId)
            .Select(game => game.GenreId)
            .SingleAsync(cancellationToken);
        var tagIds = await db.GameTagAssignments
            .Where(item => item.GameId == gameId)
            .Select(item => item.TagId)
            .ToListAsync(cancellationToken);
        var platformIds = await db.GamePlatformAssignments
            .Where(item => item.GameId == gameId)
            .Select(item => item.PlatformId)
            .ToListAsync(cancellationToken);

        Assert.Equal(GameClassificationCatalog.UnspecifiedGenreId, genreId);
        Assert.Empty(tagIds);
        Assert.Equal(
            [GameClassificationCatalog.UnspecifiedPlatformId],
            platformIds);

        var constraintException = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO game_genres (id, code, display_name, sort_order, is_active)
                VALUES (-4001, 'INVALID', '잘못된 코드', 10, TRUE)
                """,
                cancellationToken));
        Assert.Equal(PostgresErrorCodes.CheckViolation, constraintException.SqlState);
        Assert.Equal("CK_game_genres_code_format", constraintException.ConstraintName);
    }

    private async Task ExecuteAdminCommandAsync(
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(
            _fixture.CreateOwnerConnectionString("postgres"));
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(commandText, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
