using IndieFi.Web.Modules.Games.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class GameClassificationPersistenceTests
{
    private readonly PostgreSqlFixture _fixture;

    public GameClassificationPersistenceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Migration_SeedsApprovedCatalogDefinitions()
    {
        await using var db = _fixture.NewOwnerDbContext();

        var genres = await db.GameGenres
            .AsNoTracking()
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Code)
            .Select(item => new StoredDefinition(
                item.Id,
                item.Code,
                item.DisplayName,
                item.SortOrder,
                item.IsActive))
            .ToListAsync(TestContext.Current.CancellationToken);
        var tags = await db.GameTags
            .AsNoTracking()
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Code)
            .Select(item => new StoredDefinition(
                item.Id,
                item.Code,
                item.DisplayName,
                item.SortOrder,
                item.IsActive))
            .ToListAsync(TestContext.Current.CancellationToken);
        var platforms = await db.GamePlatforms
            .AsNoTracking()
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Code)
            .Select(item => new StoredDefinition(
                item.Id,
                item.Code,
                item.DisplayName,
                item.SortOrder,
                item.IsActive))
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ToStored(GameClassificationCatalog.Genres), genres);
        Assert.Equal(ToStored(GameClassificationCatalog.Tags), tags);
        Assert.Equal(ToStored(GameClassificationCatalog.Platforms), platforms);
    }

    [Fact]
    public async Task OwnerContext_CanPersistGenreTagAndPlatformRelationships()
    {
        long gameId;
        await using (var arrangeDb = _fixture.NewOwnerDbContext())
        {
            var owner = await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, balance: 0);
            var game = await ReviewTestData.CreateGameAsync(arrangeDb, owner.Id);
            game.GenreId = 12;
            arrangeDb.GameTagAssignments.AddRange(
                new GameTagAssignment { GameId = game.Id, TagId = 10 },
                new GameTagAssignment { GameId = game.Id, TagId = 16 });
            arrangeDb.GamePlatformAssignments.AddRange(
                new GamePlatformAssignment { GameId = game.Id, PlatformId = 10 },
                new GamePlatformAssignment { GameId = game.Id, PlatformId = 12 });
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            gameId = game.Id;
        }

        await using var verificationDb = _fixture.NewOwnerDbContext();
        var gameGenreId = await verificationDb.Games
            .Where(game => game.Id == gameId)
            .Select(game => game.GenreId)
            .SingleAsync(TestContext.Current.CancellationToken);
        var tagIds = await verificationDb.GameTagAssignments
            .Where(item => item.GameId == gameId)
            .OrderBy(item => item.TagId)
            .Select(item => item.TagId)
            .ToListAsync(TestContext.Current.CancellationToken);
        var platformIds = await verificationDb.GamePlatformAssignments
            .Where(item => item.GameId == gameId)
            .OrderBy(item => item.PlatformId)
            .Select(item => item.PlatformId)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(12, gameGenreId);
        Assert.Equal([10, 16], tagIds);
        Assert.Equal([10, 12], platformIds);
    }

    [Fact]
    public async Task DeleteGame_CascadesClassificationAssignments()
    {
        long gameId;
        await using (var arrangeDb = _fixture.NewOwnerDbContext())
        {
            var owner = await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, balance: 0);
            var game = await ReviewTestData.CreateGameAsync(arrangeDb, owner.Id);
            arrangeDb.GameTagAssignments.Add(
                new GameTagAssignment { GameId = game.Id, TagId = 10 });
            arrangeDb.GamePlatformAssignments.Add(
                new GamePlatformAssignment { GameId = game.Id, PlatformId = 10 });
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            gameId = game.Id;

            arrangeDb.Games.Remove(game);
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewOwnerDbContext();
        Assert.False(await verificationDb.GameTagAssignments.AnyAsync(
            item => item.GameId == gameId,
            TestContext.Current.CancellationToken));
        Assert.False(await verificationDb.GamePlatformAssignments.AnyAsync(
            item => item.GameId == gameId,
            TestContext.Current.CancellationToken));
    }

    private static StoredDefinition[] ToStored(
        IReadOnlyList<GameClassificationDefinition> definitions)
    {
        return definitions
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .Select(item => new StoredDefinition(
                item.Id,
                item.Code,
                item.DisplayName,
                item.SortOrder,
                item.IsActive))
            .ToArray();
    }

    private sealed record StoredDefinition(
        long Id,
        string Code,
        string DisplayName,
        int SortOrder,
        bool IsActive);
}
