using IndieFi.Web.Data;
using IndieFi.Web.Modules.Games.Application;
using IndieFi.Web.Modules.Games.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class GameClassificationCatalogQueryServiceTests : IDisposable
{
    private readonly PostgreSqlFixture _fixture;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public GameClassificationCatalogQueryServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetActiveAsync_ReturnsOnlyActiveOptionsInCatalogOrder()
    {
        await using var db = _fixture.NewDbContext();
        var result = await CreateService(db)
            .GetActiveAsync(TestContext.Current.CancellationToken);

        AssertOptionsMatchActiveDefinitions(result.Genres, GameClassificationCatalog.Genres);
        AssertOptionsMatchActiveDefinitions(result.Tags, GameClassificationCatalog.Tags);
        AssertOptionsMatchActiveDefinitions(result.Platforms, GameClassificationCatalog.Platforms);
    }

    [Fact]
    public async Task GetActiveAsync_AfterFirstRead_DoesNotQueryDatabaseAgain()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var firstRoundTrips = new RoundTripCountingInterceptor();
        await using var firstDb = _fixture.NewDbContext(firstRoundTrips);
        var first = await CreateService(firstDb).GetActiveAsync(cancellationToken);

        var secondRoundTrips = new RoundTripCountingInterceptor();
        await using var secondDb = _fixture.NewDbContext(secondRoundTrips);
        var second = await CreateService(secondDb).GetActiveAsync(cancellationToken);

        Assert.Equal(3, firstRoundTrips.Count);
        Assert.Equal(0, secondRoundTrips.Count);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task GetActiveAsync_WhenCacheMissesConcurrently_QueriesOnlyOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var firstRoundTrips = new RoundTripCountingInterceptor();
        var secondRoundTrips = new RoundTripCountingInterceptor();
        await using var firstDb = _fixture.NewDbContext(firstRoundTrips);
        await using var secondDb = _fixture.NewDbContext(secondRoundTrips);

        var results = await Task.WhenAll(
            CreateService(firstDb).GetActiveAsync(cancellationToken),
            CreateService(secondDb).GetActiveAsync(cancellationToken));

        Assert.Equal(3, firstRoundTrips.Count + secondRoundTrips.Count);
        Assert.Same(results[0], results[1]);
    }

    [Fact]
    public async Task GetForGameAsync_IncludesItsInactiveSelectionsOnly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = _fixture.NewOwnerDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var selectedTag = await db.GameTags.SingleAsync(
            item => item.Id == 10,
            cancellationToken);
        selectedTag.IsActive = false;
        db.GameTags.Add(new GameTag
        {
            Id = -3001,
            Code = "inactive-unselected",
            DisplayName = "선택하지 않은 비활성 태그",
            SortOrder = 5,
            IsActive = false,
        });
        var owner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var game = await ReviewTestData.CreateGameAsync(db, owner.Id);
        db.GameTagAssignments.Add(new GameTagAssignment
        {
            GameId = game.Id,
            TagId = selectedTag.Id,
        });
        db.GamePlatformAssignments.Add(new GamePlatformAssignment
        {
            GameId = game.Id,
            PlatformId = GameClassificationCatalog.UnspecifiedPlatformId,
        });
        await db.SaveChangesAsync(cancellationToken);

        var result = await CreateService(db)
            .GetForGameAsync(game.Id, cancellationToken);

        Assert.NotNull(result);
        Assert.Equal(GameClassificationCatalog.UnspecifiedGenreId, result.SelectedGenreId);
        Assert.Equal([selectedTag.Id], result.SelectedTagIds);
        Assert.Equal(
            [GameClassificationCatalog.UnspecifiedPlatformId],
            result.SelectedPlatformIds);

        Assert.False(Assert.Single(
            result.Options.Genres,
            item => item.Id == GameClassificationCatalog.UnspecifiedGenreId).IsActive);
        Assert.False(Assert.Single(
            result.Options.Tags,
            item => item.Id == selectedTag.Id).IsActive);
        Assert.False(Assert.Single(
            result.Options.Platforms,
            item => item.Id == GameClassificationCatalog.UnspecifiedPlatformId).IsActive);
        Assert.DoesNotContain(result.Options.Tags, item => item.Id == -3001);

        await transaction.RollbackAsync(cancellationToken);
    }

    [Fact]
    public async Task GetDetailAsync_ReturnsSelectedValuesInCatalogOrderIncludingInactive()
    {
        long gameId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var owner = await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, balance: 0);
            var game = await ReviewTestData.CreateGameAsync(arrangeDb, owner.Id);
            arrangeDb.GameTagAssignments.AddRange(
                new GameTagAssignment { GameId = game.Id, TagId = 28 },
                new GameTagAssignment { GameId = game.Id, TagId = 10 });
            arrangeDb.GamePlatformAssignments.AddRange(
                new GamePlatformAssignment { GameId = game.Id, PlatformId = 12 },
                new GamePlatformAssignment
                {
                    GameId = game.Id,
                    PlatformId = GameClassificationCatalog.UnspecifiedPlatformId,
                });
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            gameId = game.Id;
        }

        await using var db = _fixture.NewDbContext();
        var result = await CreateService(db)
            .GetDetailAsync(gameId, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(GameClassificationCatalog.UnspecifiedCode, result.Genre.Code);
        Assert.False(result.Genre.IsActive);
        Assert.Equal(
            ["single-player", "pixel-art"],
            result.Tags.Select(item => item.Code));
        Assert.Equal(
            ["unspecified", "linux"],
            result.Platforms.Select(item => item.Code));
        Assert.False(result.Platforms[0].IsActive);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MaxValue)]
    public async Task GetForGameAsync_WhenGameIsUnavailable_ReturnsNull(long gameId)
    {
        await using var db = _fixture.NewDbContext();
        var result = await CreateService(db)
            .GetForGameAsync(gameId, TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    public void Dispose() => _cache.Dispose();

    private GameClassificationCatalogQueryService CreateService(AppDbContext db) =>
        new(db, _cache);

    private static void AssertOptionsMatchActiveDefinitions(
        IReadOnlyList<GameClassificationOption> actual,
        IReadOnlyList<GameClassificationDefinition> definitions)
    {
        var expected = definitions
            .Where(item => item.IsActive)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .Select(item => new GameClassificationOption(
                item.Id,
                item.Code,
                item.DisplayName,
                item.IsActive))
            .ToArray();

        Assert.Equal(expected, actual);
        Assert.All(actual, item => Assert.True(item.IsActive));
    }
}
