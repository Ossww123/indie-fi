using System.Data.Common;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Domain;
using IndieFi.Web.Modules.Games.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class GamePlayLinkManagementServiceTests
{
    private readonly PostgreSqlFixture _fixture;

    public GamePlayLinkManagementServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task UpdateAsync_WhenCallerOwnsGame_ReplacesNormalizesAndOrdersLinks()
    {
        var (ownerId, gameId) = await CreateOwnedGameAsync();

        UpdateGamePlayLinksResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await new GamePlayLinkManagementService(serviceDb).UpdateAsync(
                gameId,
                ownerId,
                [
                    new GamePlayLinkInput(
                        "  Browser build  ",
                        GamePlayLinkProvider.DeveloperWebsite,
                        GamePlayLinkPlatform.Web,
                        "  https://example.com/play  "),
                    new GamePlayLinkInput(
                        "Steam",
                        GamePlayLinkProvider.Steam,
                        null,
                        "https://store.steampowered.com/app/123"),
                ],
                TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var links = await verificationDb.GamePlayLinks
            .Where(link => link.GameId == gameId)
            .OrderBy(link => link.SortOrder)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateGamePlayLinksOutcome.Updated, result.Outcome);
        Assert.Empty(result.Violations);
        Assert.Equal(2, links.Count);
        Assert.Equal("Browser build", links[0].DisplayName);
        Assert.Equal("https://example.com/play", links[0].Url);
        Assert.Equal(GamePlayLinkProvider.DeveloperWebsite, links[0].Provider);
        Assert.Equal(GamePlayLinkPlatform.Web, links[0].TargetPlatform);
        Assert.Equal([0, 1], links.Select(link => link.SortOrder));
    }

    [Fact]
    public async Task UpdateAsync_WithNewCollection_CanEditRemoveAndReorderExistingLinks()
    {
        var (ownerId, gameId) = await CreateOwnedGameAsync();
        await AddLinksAsync(
            gameId,
            ("First", "https://example.com/first"),
            ("Second", "https://example.com/second"),
            ("Remove me", "https://example.com/remove"));

        await using (var serviceDb = _fixture.NewDbContext())
        {
            var result = await new GamePlayLinkManagementService(serviceDb).UpdateAsync(
                gameId,
                ownerId,
                [
                    new GamePlayLinkInput(
                        "Second updated",
                        GamePlayLinkProvider.ItchIo,
                        GamePlayLinkPlatform.Multiple,
                        "https://example.com/second-new"),
                    new GamePlayLinkInput(
                        "First",
                        GamePlayLinkProvider.Other,
                        null,
                        "https://example.com/first"),
                ],
                TestContext.Current.CancellationToken);

            Assert.Equal(UpdateGamePlayLinksOutcome.Updated, result.Outcome);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var links = await verificationDb.GamePlayLinks
            .Where(link => link.GameId == gameId)
            .OrderBy(link => link.SortOrder)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["Second updated", "First"], links.Select(link => link.DisplayName));
        Assert.Equal([0, 1], links.Select(link => link.SortOrder));
        Assert.DoesNotContain(links, link => link.DisplayName == "Remove me");
    }

    [Fact]
    public async Task UpdateAsync_WithEmptyCollection_RemovesEveryLink()
    {
        var (ownerId, gameId) = await CreateOwnedGameAsync();
        await AddLinksAsync(gameId, ("Old link", "https://example.com/old"));

        await using (var serviceDb = _fixture.NewDbContext())
        {
            var result = await new GamePlayLinkManagementService(serviceDb).UpdateAsync(
                gameId,
                ownerId,
                [],
                TestContext.Current.CancellationToken);

            Assert.Equal(UpdateGamePlayLinksOutcome.Updated, result.Outcome);
        }

        await using var verificationDb = _fixture.NewDbContext();
        Assert.False(await verificationDb.GamePlayLinks.AnyAsync(
            link => link.GameId == gameId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateAsync_WithInvalidCollection_ReportsAllFieldsAndKeepsExistingLinks()
    {
        var (ownerId, gameId) = await CreateOwnedGameAsync();
        await AddLinksAsync(gameId, ("Original", "https://example.com/original"));
        var candidates = Enumerable.Range(0, 6)
            .Select(index => new GamePlayLinkInput(
                index == 0 ? "   " : $"Link {index}",
                index == 0 ? (GamePlayLinkProvider)999 : GamePlayLinkProvider.Other,
                index == 0 ? (GamePlayLinkPlatform)999 : null,
                index == 0 ? "http://127.0.0.1/play" : $"https://example.com/{index}"))
            .ToArray();

        UpdateGamePlayLinksResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await new GamePlayLinkManagementService(serviceDb).UpdateAsync(
                gameId,
                ownerId,
                candidates,
                TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var remaining = await verificationDb.GamePlayLinks
            .SingleAsync(link => link.GameId == gameId, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateGamePlayLinksOutcome.Invalid, result.Outcome);
        Assert.Equal(
            [
                GamePlayLinkManagementService.LinksField,
                GamePlayLinkManagementService.DisplayNameField(0),
                GamePlayLinkManagementService.ProviderField(0),
                GamePlayLinkManagementService.TargetPlatformField(0),
                GamePlayLinkManagementService.UrlField(0),
            ],
            result.Violations.Select(violation => violation.Field));
        Assert.Equal("Original", remaining.DisplayName);
    }

    [Fact]
    public async Task UpdateAsync_WhenCallerDoesNotOwnGame_ReturnsNotFoundAndKeepsLinks()
    {
        var (ownerId, gameId) = await CreateOwnedGameAsync();
        var otherUserId = await CreateUserAsync();
        await AddLinksAsync(gameId, ("Original", "https://example.com/original"));

        UpdateGamePlayLinksResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await new GamePlayLinkManagementService(serviceDb).UpdateAsync(
                gameId,
                otherUserId,
                [NewLink("Attacker")],
                TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var remaining = await verificationDb.GamePlayLinks
            .SingleAsync(link => link.GameId == gameId, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateGamePlayLinksOutcome.NotFound, result.Outcome);
        Assert.Empty(result.Violations);
        Assert.Equal("Original", remaining.DisplayName);
        Assert.NotEqual(ownerId, otherUserId);
    }

    [Fact]
    public async Task UpdateAsync_WhenGameDoesNotExist_ReturnsSameNotFoundOutcome()
    {
        var ownerId = await CreateUserAsync();

        await using var serviceDb = _fixture.NewDbContext();
        var result = await new GamePlayLinkManagementService(serviceDb).UpdateAsync(
            long.MaxValue,
            ownerId,
            [NewLink("Unused")],
            TestContext.Current.CancellationToken);

        Assert.Equal(UpdateGamePlayLinksOutcome.NotFound, result.Outcome);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public async Task UpdateAsync_WhenAccountIsSuspended_ReturnsAccountUnavailableAndKeepsLinks()
    {
        var (ownerId, gameId) = await CreateOwnedGameAsync();
        await AddLinksAsync(gameId, ("Original", "https://example.com/original"));
        await using (var suspensionDb = _fixture.NewDbContext())
        {
            var owner = await suspensionDb.Users.SingleAsync(
                user => user.Id == ownerId,
                TestContext.Current.CancellationToken);
            owner.AccountStatus = AccountStatus.IndefinitelySuspended;
            owner.SuspendedUntil = null;
            await suspensionDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        UpdateGamePlayLinksResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await new GamePlayLinkManagementService(serviceDb).UpdateAsync(
                gameId,
                ownerId,
                [NewLink("Blocked")],
                TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var remaining = await verificationDb.GamePlayLinks
            .SingleAsync(link => link.GameId == gameId, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateGamePlayLinksOutcome.AccountUnavailable, result.Outcome);
        Assert.Equal("Original", remaining.DisplayName);
    }

    [Fact]
    public async Task UpdateAsync_WhenInsertFails_RollsBackDeletedLinks()
    {
        var (ownerId, gameId) = await CreateOwnedGameAsync();
        await AddLinksAsync(gameId, ("Original", "https://example.com/original"));

        await using (var serviceDb = _fixture.NewDbContext(new ThrowOnLinkInsertInterceptor()))
        {
            var exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
                new GamePlayLinkManagementService(serviceDb).UpdateAsync(
                    gameId,
                    ownerId,
                    [NewLink("Replacement")],
                    TestContext.Current.CancellationToken));
            Assert.IsType<InvalidOperationException>(exception.InnerException);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var remaining = await verificationDb.GamePlayLinks
            .SingleAsync(link => link.GameId == gameId, TestContext.Current.CancellationToken);
        Assert.Equal("Original", remaining.DisplayName);
    }

    private async Task<(long OwnerId, long GameId)> CreateOwnedGameAsync()
    {
        await using var db = _fixture.NewDbContext();
        var owner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var game = await ReviewTestData.CreateGameAsync(db, owner.Id);
        return (owner.Id, game.Id);
    }

    private async Task<long> CreateUserAsync()
    {
        await using var db = _fixture.NewDbContext();
        return (await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0)).Id;
    }

    private async Task AddLinksAsync(
        long gameId,
        params (string DisplayName, string Url)[] links)
    {
        await using var db = _fixture.NewDbContext();
        db.GamePlayLinks.AddRange(links.Select((link, sortOrder) => new GamePlayLink
        {
            GameId = gameId,
            DisplayName = link.DisplayName,
            Provider = GamePlayLinkProvider.Other,
            Url = link.Url,
            SortOrder = sortOrder,
        }));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static GamePlayLinkInput NewLink(string displayName) => new(
        displayName,
        GamePlayLinkProvider.Other,
        null,
        $"https://example.com/{displayName.ToLowerInvariant()}");

    private sealed class ThrowOnLinkInsertInterceptor : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(
                    "INSERT INTO game_play_links",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Forced game play link insert failure.");
            }

            return new ValueTask<InterceptionResult<DbDataReader>>(result);
        }
    }
}
