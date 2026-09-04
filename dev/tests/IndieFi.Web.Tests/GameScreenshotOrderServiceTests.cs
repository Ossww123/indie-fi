using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class GameScreenshotOrderServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 17, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlFixture _fixture;

    public GameScreenshotOrderServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ReorderAsync_ReplacesTheCompleteReadyOrderWithoutUniqueConflicts()
    {
        await ResetAsync();
        var arranged = await ArrangeGameAsync();
        var first = ReadyScreenshot(arranged, sortOrder: 0);
        var second = ReadyScreenshot(arranged, sortOrder: 1);
        var third = ReadyScreenshot(arranged, sortOrder: 2);
        await AddMediaAsync(first, second, third);

        await using var db = _fixture.NewDbContext();
        var result = await new GameScreenshotOrderService(
            db,
            new FixedTimeProvider(Now))
            .ReorderAsync(
                arranged.OwnerId,
                arranged.GameId,
                [third.Id, first.Id, second.Id],
                TestContext.Current.CancellationToken);

        Assert.Equal(GameScreenshotOrderOutcome.Reordered, result.Outcome);
        Assert.Equal([third.Id, first.Id, second.Id], result.MediaIds);

        await using var verificationDb = _fixture.NewDbContext();
        var ordered = await verificationDb.MediaAssets
            .AsNoTracking()
            .Where(item => item.GameId == arranged.GameId
                && item.AttachmentKind == MediaAttachmentKind.GameScreenshot
                && item.Status == MediaStatus.Ready)
            .OrderBy(item => item.SortOrder)
            .Select(item => new { item.Id, item.SortOrder, item.PublishedAt })
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal([third.Id, first.Id, second.Id], ordered.Select(item => item.Id));
        Assert.Equal([0, 1, 2], ordered.Select(item => item.SortOrder));
        Assert.All(ordered, item => Assert.Equal(Now.AddMinutes(-5), item.PublishedAt));
    }

    [Fact]
    public async Task ReorderAsync_RejectsIncompleteDuplicateAndForeignSetsWithoutMutation()
    {
        await ResetAsync();
        var arranged = await ArrangeGameAsync();
        var other = await ArrangeGameAsync();
        var first = ReadyScreenshot(arranged, sortOrder: 0);
        var second = ReadyScreenshot(arranged, sortOrder: 1);
        var foreign = ReadyScreenshot(other, sortOrder: 0);
        await AddMediaAsync(first, second, foreign);

        await using var db = _fixture.NewDbContext();
        var service = new GameScreenshotOrderService(
            db,
            new FixedTimeProvider(Now));
        var incomplete = await service.ReorderAsync(
            arranged.OwnerId,
            arranged.GameId,
            [second.Id],
            TestContext.Current.CancellationToken);
        var duplicate = await service.ReorderAsync(
            arranged.OwnerId,
            arranged.GameId,
            [first.Id, first.Id],
            TestContext.Current.CancellationToken);
        var mixed = await service.ReorderAsync(
            arranged.OwnerId,
            arranged.GameId,
            [first.Id, foreign.Id],
            TestContext.Current.CancellationToken);

        Assert.Equal(GameScreenshotOrderOutcome.InvalidOrder, incomplete.Outcome);
        Assert.Equal(GameScreenshotOrderOutcome.InvalidOrder, duplicate.Outcome);
        Assert.Equal(GameScreenshotOrderOutcome.InvalidOrder, mixed.Outcome);

        await using var verificationDb = _fixture.NewDbContext();
        var unchanged = await verificationDb.MediaAssets
            .Where(item => item.GameId == arranged.GameId
                && item.Status == MediaStatus.Ready)
            .OrderBy(item => item.SortOrder)
            .Select(item => item.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal([first.Id, second.Id], unchanged);
    }

    [Fact]
    public async Task ReorderAsync_HidesUnownedGamesAndBlocksUnavailableAccounts()
    {
        await ResetAsync();
        var arranged = await ArrangeGameAsync();
        var screenshot = ReadyScreenshot(arranged, sortOrder: 0);
        await AddMediaAsync(screenshot);

        await using var db = _fixture.NewDbContext();
        var service = new GameScreenshotOrderService(
            db,
            new FixedTimeProvider(Now));
        var foreign = await service.ReorderAsync(
            arranged.OtherUserId,
            arranged.GameId,
            [screenshot.Id],
            TestContext.Current.CancellationToken);

        await using (var ownerDb = _fixture.NewOwnerDbContext())
        {
            await ownerDb.Users
                .Where(item => item.Id == arranged.OwnerId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        item => item.AccountStatus,
                        Modules.Accounts.Domain.AccountStatus.IndefinitelySuspended),
                    TestContext.Current.CancellationToken);
        }

        var unavailable = await service.ReorderAsync(
            arranged.OwnerId,
            arranged.GameId,
            [screenshot.Id],
            TestContext.Current.CancellationToken);

        Assert.Equal(GameScreenshotOrderOutcome.GameNotFound, foreign.Outcome);
        Assert.Equal(
            GameScreenshotOrderOutcome.AccountUnavailable,
            unavailable.Outcome);
    }

    private async Task<ArrangedGame> ArrangeGameAsync()
    {
        await using var db = _fixture.NewDbContext();
        var owner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var other = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var game = new Game
        {
            OwnerId = owner.Id,
            Title = $"Screenshot order {Guid.NewGuid():N}",
        };
        db.Games.Add(game);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return new ArrangedGame(owner.Id, other.Id, game.Id);
    }

    private async Task AddMediaAsync(params MediaAsset[] media)
    {
        await using var db = _fixture.NewDbContext();
        db.MediaAssets.AddRange(media);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static MediaAsset ReadyScreenshot(
        ArrangedGame game,
        int sortOrder) => new()
        {
            Id = Guid.NewGuid(),
            OwnerUserId = game.OwnerId,
            GameId = game.GameId,
            AttachmentKind = MediaAttachmentKind.GameScreenshot,
            Status = MediaStatus.Ready,
            DeclaredContentType = "image/png",
            DeclaredBytes = 128,
            SourceContentType = "image/png",
            SourceBytes = 128,
            SourceETag = $"\"{Guid.NewGuid():N}\"",
            DetectedContentType = "image/png",
            PixelWidth = 80,
            PixelHeight = 45,
            FrameCount = 1,
            IsCurrent = true,
            SortOrder = sortOrder,
            PublishedAt = Now.AddMinutes(-5),
            CreatedAt = Now.AddMinutes(-10),
            UpdatedAt = Now.AddMinutes(-5),
            StatusChangedAt = Now.AddMinutes(-5),
            UploadExpiresAt = Now.AddHours(23).AddMinutes(50),
        };

    private async Task ResetAsync()
    {
        await using var db = _fixture.NewOwnerDbContext();
        await db.MediaUploadAttempts.ExecuteDeleteAsync(
            TestContext.Current.CancellationToken);
        await db.MediaAssets.ExecuteDeleteAsync(
            TestContext.Current.CancellationToken);
    }

    private sealed record ArrangedGame(
        long OwnerId,
        long OtherUserId,
        long GameId);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
