using System.Data.Common;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Games.Application;
using IndieFi.Web.Modules.Games.Domain;
using IndieFi.Web.Modules.Notifications.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class GameClassificationManagementServiceTests
{
    private readonly PostgreSqlFixture _fixture;

    public GameClassificationManagementServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CreateAsync_StoresGameLinksAndClassificationsTogether()
    {
        var ownerId = await CreateUserAsync();
        var title = $"Classified create {Guid.NewGuid():N}";
        CreateGameResult result;
        await using (var db = _fixture.NewDbContext())
        {
            result = await NewService(db).CreateAsync(
                ownerId,
                title,
                "분류와 함께 저장",
                new GameClassificationInput(
                    "role-playing",
                    ["pixel-art", "single-player"],
                    ["linux", "windows"]),
                [NewLink("Browser build")],
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(CreateGameOutcome.Created, result.Outcome);
        await using var verificationDb = _fixture.NewDbContext();
        var game = await verificationDb.Games.SingleAsync(
            item => item.Id == result.GameId,
            TestContext.Current.CancellationToken);
        var tagIds = await verificationDb.GameTagAssignments
            .Where(item => item.GameId == game.Id)
            .OrderBy(item => item.TagId)
            .Select(item => item.TagId)
            .ToListAsync(TestContext.Current.CancellationToken);
        var platformIds = await verificationDb.GamePlatformAssignments
            .Where(item => item.GameId == game.Id)
            .OrderBy(item => item.PlatformId)
            .Select(item => item.PlatformId)
            .ToListAsync(TestContext.Current.CancellationToken);
        var link = await verificationDb.GamePlayLinks.SingleAsync(
            item => item.GameId == game.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(12, game.GenreId);
        Assert.Equal([10, 28], tagIds);
        Assert.Equal([10, 12], platformIds);
        Assert.Equal("Browser build", link.DisplayName);
    }

    [Fact]
    public async Task CreateAsync_WithInvalidClassification_WritesNothing()
    {
        var ownerId = await CreateUserAsync();
        var title = $"Invalid classified create {Guid.NewGuid():N}";
        CreateGameResult result;
        await using (var db = _fixture.NewDbContext())
        {
            result = await NewService(db).CreateAsync(
                ownerId,
                title,
                null,
                new GameClassificationInput(
                    "unspecified",
                    ["single-player", "single-player"],
                    []),
                [NewLink("Should not persist")],
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(CreateGameOutcome.Invalid, result.Outcome);
        Assert.Contains(result.Violations, item =>
            item.Field == GameClassificationSelectionService.GenreField);
        Assert.Contains(result.Violations, item =>
            item.Field == GameClassificationSelectionService.TagsField);
        Assert.Contains(result.Violations, item =>
            item.Field == GameClassificationSelectionService.PlatformsField);

        await using var verificationDb = _fixture.NewDbContext();
        Assert.False(await verificationDb.Games.AnyAsync(
            item => item.Title == title,
            TestContext.Current.CancellationToken));
        Assert.False(await verificationDb.GamePlayLinks.AnyAsync(
            item => item.DisplayName == "Should not persist",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateAsync_ReplacesGameLinksAndClassificationsTogether()
    {
        var arranged = await CreateClassifiedGameAsync();
        UpdateGameResult result;
        await using (var db = _fixture.NewDbContext())
        {
            result = await NewService(db).UpdateAsync(
                arranged.GameId,
                arranged.OwnerId,
                "Updated classified game",
                "Updated description",
                new GameClassificationInput(
                    "role-playing",
                    ["pixel-art"],
                    ["linux", "windows"]),
                [NewLink("Replacement link")],
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(UpdateGameOutcome.Updated, result.Outcome);
        await using var verificationDb = _fixture.NewDbContext();
        var game = await verificationDb.Games.SingleAsync(
            item => item.Id == arranged.GameId,
            TestContext.Current.CancellationToken);
        var tagIds = await verificationDb.GameTagAssignments
            .Where(item => item.GameId == arranged.GameId)
            .Select(item => item.TagId)
            .ToListAsync(TestContext.Current.CancellationToken);
        var platformIds = await verificationDb.GamePlatformAssignments
            .Where(item => item.GameId == arranged.GameId)
            .OrderBy(item => item.PlatformId)
            .Select(item => item.PlatformId)
            .ToListAsync(TestContext.Current.CancellationToken);
        var link = await verificationDb.GamePlayLinks.SingleAsync(
            item => item.GameId == arranged.GameId,
            TestContext.Current.CancellationToken);

        Assert.Equal("Updated classified game", game.Title);
        Assert.Equal(12, game.GenreId);
        Assert.Equal([28], tagIds);
        Assert.Equal([10, 12], platformIds);
        Assert.Equal("Replacement link", link.DisplayName);
    }

    [Fact]
    public async Task UpdateAsync_WithInvalidClassification_PreservesEverything()
    {
        var arranged = await CreateClassifiedGameAsync();
        UpdateGameResult result;
        await using (var db = _fixture.NewDbContext())
        {
            result = await NewService(db).UpdateAsync(
                arranged.GameId,
                arranged.OwnerId,
                "Should not update",
                "Should not update",
                new GameClassificationInput(
                    "unspecified",
                    ["unknown-tag"],
                    []),
                [NewLink("Should not replace")],
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(UpdateGameOutcome.Invalid, result.Outcome);
        await AssertOriginalStateAsync(arranged);
    }

    [Fact]
    public async Task UpdateAsync_WhenLinkInsertFails_RollsBackClassificationChanges()
    {
        var arranged = await CreateClassifiedGameAsync();
        await using (var db = _fixture.NewDbContext(new ThrowOnInsertInterceptor(
            "INSERT INTO game_play_links")))
        {
            var exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
                NewService(db).UpdateAsync(
                    arranged.GameId,
                    arranged.OwnerId,
                    "Should roll back",
                    null,
                    new GameClassificationInput(
                        "role-playing",
                        ["pixel-art"],
                        ["linux"]),
                    [NewLink("Forced failure")],
                    TestContext.Current.CancellationToken));
            Assert.IsType<InvalidOperationException>(exception.InnerException);
        }

        await AssertOriginalStateAsync(arranged);
    }

    [Fact]
    public async Task CreateAsync_WhenClassificationInsertFails_RollsBackGame()
    {
        var ownerId = await CreateUserAsync();
        var title = $"Classification rollback {Guid.NewGuid():N}";
        await using (var db = _fixture.NewDbContext(new ThrowOnInsertInterceptor(
            "INSERT INTO game_tag_assignments")))
        {
            var exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
                NewService(db).CreateAsync(
                    ownerId,
                    title,
                    null,
                    new GameClassificationInput(
                        "action",
                        ["single-player"],
                        ["windows"]),
                    [NewLink("Rolled back with game")],
                    TestContext.Current.CancellationToken));
            Assert.IsType<InvalidOperationException>(exception.InnerException);
        }

        await using var verificationDb = _fixture.NewDbContext();
        Assert.False(await verificationDb.Games.AnyAsync(
            item => item.Title == title,
            TestContext.Current.CancellationToken));
        Assert.False(await verificationDb.GamePlayLinks.AnyAsync(
            item => item.DisplayName == "Rolled back with game",
            TestContext.Current.CancellationToken));
    }

    private async Task<long> CreateUserAsync()
    {
        await using var db = _fixture.NewDbContext();
        return (await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0)).Id;
    }

    private async Task<ArrangedGame> CreateClassifiedGameAsync()
    {
        await using var db = _fixture.NewDbContext();
        var owner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var game = await ReviewTestData.CreateGameAsync(db, owner.Id);
        game.GenreId = 10;
        db.GameTagAssignments.Add(new GameTagAssignment
        {
            GameId = game.Id,
            TagId = 10,
        });
        db.GamePlatformAssignments.Add(new GamePlatformAssignment
        {
            GameId = game.Id,
            PlatformId = 10,
        });
        db.GamePlayLinks.Add(new GamePlayLink
        {
            GameId = game.Id,
            DisplayName = "Original link",
            Provider = GamePlayLinkProvider.Other,
            Url = "https://example.com/original",
            SortOrder = 0,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return new ArrangedGame(owner.Id, game.Id, game.Title);
    }

    private async Task AssertOriginalStateAsync(ArrangedGame arranged)
    {
        await using var db = _fixture.NewDbContext();
        var game = await db.Games.SingleAsync(
            item => item.Id == arranged.GameId,
            TestContext.Current.CancellationToken);
        var tagIds = await db.GameTagAssignments
            .Where(item => item.GameId == arranged.GameId)
            .Select(item => item.TagId)
            .ToListAsync(TestContext.Current.CancellationToken);
        var platformIds = await db.GamePlatformAssignments
            .Where(item => item.GameId == arranged.GameId)
            .Select(item => item.PlatformId)
            .ToListAsync(TestContext.Current.CancellationToken);
        var link = await db.GamePlayLinks.SingleAsync(
            item => item.GameId == arranged.GameId,
            TestContext.Current.CancellationToken);

        Assert.Equal(arranged.Title, game.Title);
        Assert.Equal(10, game.GenreId);
        Assert.Equal([10], tagIds);
        Assert.Equal([10], platformIds);
        Assert.Equal("Original link", link.DisplayName);
    }

    private static GamePlayLinkInput NewLink(string displayName) => new(
        displayName,
        GamePlayLinkProvider.Other,
        null,
        $"https://example.com/{displayName.ToLowerInvariant().Replace(' ', '-')}");

    private static GameManagementService NewService(AppDbContext db) => new(
        db,
        new TestSocialNotificationFanoutPublisher());

    private sealed record ArrangedGame(long OwnerId, long GameId, string Title);

    private sealed class ThrowOnInsertInterceptor(string commandFragment)
        : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(
                    commandFragment,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Forced classification transaction failure.");
            }

            return new ValueTask<InterceptionResult<DbDataReader>>(result);
        }
    }
}
