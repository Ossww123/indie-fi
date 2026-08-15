using IndieFi.Web.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class GamePlayLinkPersistenceTests
{
    private readonly PostgreSqlFixture _fixture;

    public GamePlayLinkPersistenceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ApplicationRole_CanStoreFiveOrderedLinks()
    {
        var gameId = await CreateGameAsync();
        await using (var db = _fixture.NewDbContext())
        {
            db.GamePlayLinks.AddRange(
                Enumerable.Range(0, 5).Select(sortOrder => new GamePlayLink
                {
                    GameId = gameId,
                    DisplayName = $"Play build {sortOrder + 1}",
                    Provider = GamePlayLinkProvider.Other,
                    TargetPlatform = GamePlayLinkPlatform.Web,
                    Url = $"https://example.com/build/{sortOrder + 1}",
                    SortOrder = sortOrder,
                }));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var links = await verificationDb.GamePlayLinks
            .Where(link => link.GameId == gameId)
            .OrderBy(link => link.SortOrder)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(5, links.Count);
        Assert.Equal([0, 1, 2, 3, 4], links.Select(link => link.SortOrder));
        Assert.All(links, link => Assert.Equal(GamePlayLinkPlatform.Web, link.TargetPlatform));
    }

    [Fact]
    public async Task Insert_WhenSortOrderExceedsLinkLimit_IsRejectedByDatabase()
    {
        var gameId = await CreateGameAsync();
        await using var db = _fixture.NewDbContext();
        db.GamePlayLinks.Add(CreateLink(gameId, sortOrder: 5));

        await Assert.ThrowsAsync<DbUpdateException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Insert_WhenGameHasDuplicateSortOrder_IsRejectedByDatabase()
    {
        var gameId = await CreateGameAsync();
        await using var db = _fixture.NewDbContext();
        db.GamePlayLinks.AddRange(
            CreateLink(gameId, sortOrder: 0),
            CreateLink(gameId, sortOrder: 0));

        await Assert.ThrowsAsync<DbUpdateException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Insert_WhenProviderIsUnknown_IsRejectedByDatabase()
    {
        var gameId = await CreateGameAsync();
        await using var db = _fixture.NewDbContext();
        var link = CreateLink(gameId, sortOrder: 0);
        link.Provider = (GamePlayLinkProvider)999;
        db.GamePlayLinks.Add(link);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    private async Task<long> CreateGameAsync()
    {
        await using var db = _fixture.NewDbContext();
        var owner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var game = await ReviewTestData.CreateGameAsync(db, owner.Id);
        return game.Id;
    }

    private static GamePlayLink CreateLink(long gameId, int sortOrder) => new()
    {
        GameId = gameId,
        DisplayName = "Play game",
        Provider = GamePlayLinkProvider.Other,
        Url = "https://example.com/play",
        SortOrder = sortOrder,
    };
}
