using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class GameMediaQueryServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 17, 8, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlFixture _fixture;

    public GameMediaQueryServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetOwnerAsync_ReturnsOwnedActiveStatesAndOnlyReadyPreviews()
    {
        await ResetAsync();
        var arranged = await ArrangeGameAsync();
        var currentCover = Media(
            arranged,
            MediaAttachmentKind.GameCover,
            MediaStatus.Ready);
        var replacement = Media(
            arranged,
            MediaAttachmentKind.GameCover,
            MediaStatus.ProcessingPending);
        var screenshot = Media(
            arranged,
            MediaAttachmentKind.GameScreenshot,
            MediaStatus.Ready,
            sortOrder: 0);
        var failed = Media(
            arranged,
            MediaAttachmentKind.GameScreenshot,
            MediaStatus.Failed);
        failed.FailureCode = "image_invalid_format";
        var deleting = Media(
            arranged,
            MediaAttachmentKind.GameScreenshot,
            MediaStatus.DeletePending);
        await AddMediaAsync(currentCover, replacement, screenshot, failed, deleting);
        var storage = new RecordingObjectStorage();

        await using var db = _fixture.NewDbContext();
        var service = new GameMediaQueryService(db, storage);
        var result = await service.GetOwnerAsync(
            arranged.OwnerId,
            arranged.GameId,
            TestContext.Current.CancellationToken);
        var foreign = await service.GetOwnerAsync(
            arranged.OtherUserId,
            arranged.GameId,
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Null(foreign);
        Assert.Equal([currentCover.Id, replacement.Id],
            result.Covers.Select(item => item.MediaId));
        Assert.Equal([screenshot.Id, failed.Id],
            result.Screenshots.Select(item => item.MediaId));
        Assert.NotNull(result.Covers[0].PreviewUrl);
        Assert.Null(result.Covers[1].PreviewUrl);
        Assert.NotNull(result.Screenshots[0].PreviewUrl);
        Assert.Null(result.Screenshots[1].PreviewUrl);
        Assert.All(storage.RequestedKeys, key =>
            Assert.Equal(MediaDerivedVariant.GameList, key.Variant));
    }

    [Fact]
    public async Task GetPublicDetailAsync_ReturnsOnlyReadyCurrentMediaInOrder()
    {
        await ResetAsync();
        var arranged = await ArrangeGameAsync();
        var cover = Media(
            arranged,
            MediaAttachmentKind.GameCover,
            MediaStatus.Ready);
        var second = Media(
            arranged,
            MediaAttachmentKind.GameScreenshot,
            MediaStatus.Ready,
            sortOrder: 1);
        var first = Media(
            arranged,
            MediaAttachmentKind.GameScreenshot,
            MediaStatus.Ready,
            sortOrder: 0);
        var pending = Media(
            arranged,
            MediaAttachmentKind.GameScreenshot,
            MediaStatus.ProcessingPending);
        await AddMediaAsync(cover, second, first, pending);
        var storage = new RecordingObjectStorage();

        await using var db = _fixture.NewDbContext();
        var service = new GameMediaQueryService(db, storage);
        var result = await service.GetPublicDetailAsync(
            arranged.GameId,
            TestContext.Current.CancellationToken);
        var missing = await service.GetPublicDetailAsync(
            long.MaxValue,
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(cover.Id, result.Cover?.MediaId);
        Assert.Equal([first.Id, second.Id],
            result.Screenshots.Select(item => item.MediaId));
        Assert.Null(missing);
        Assert.Equal(3, storage.RequestedKeys.Count);
        Assert.All(storage.RequestedKeys, key =>
            Assert.Equal(MediaDerivedVariant.GameDisplay, key.Variant));
    }

    [Fact]
    public async Task GetPublicCoversAsync_BatchesReadyListVariants()
    {
        await ResetAsync();
        var firstGame = await ArrangeGameAsync();
        var secondGame = await ArrangeGameAsync();
        var ready = Media(
            firstGame,
            MediaAttachmentKind.GameCover,
            MediaStatus.Ready);
        var pending = Media(
            secondGame,
            MediaAttachmentKind.GameCover,
            MediaStatus.ProcessingPending);
        await AddMediaAsync(ready, pending);
        var storage = new RecordingObjectStorage();

        await using var db = _fixture.NewDbContext();
        var service = new GameMediaQueryService(db, storage);
        var result = await service.GetPublicCoversAsync(
            [firstGame.GameId, secondGame.GameId, firstGame.GameId],
            TestContext.Current.CancellationToken);

        var cover = Assert.Single(result);
        Assert.Equal(firstGame.GameId, cover.Key);
        Assert.Equal(ready.Id, cover.Value.MediaId);
        var requested = Assert.Single(storage.RequestedKeys);
        Assert.Equal(MediaDerivedVariant.GameList, requested.Variant);
    }

    private async Task<ArrangedGame> ArrangeGameAsync()
    {
        await using var db = _fixture.NewDbContext();
        var owner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var other = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var game = new Game
        {
            OwnerId = owner.Id,
            Title = $"Media query {Guid.NewGuid():N}",
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

    private static MediaAsset Media(
        ArrangedGame game,
        MediaAttachmentKind kind,
        MediaStatus status,
        int? sortOrder = null)
    {
        var ready = status == MediaStatus.Ready;
        var hasSource = status is MediaStatus.ProcessingPending
            or MediaStatus.Processing
            or MediaStatus.Ready;
        return new MediaAsset
        {
            Id = Guid.NewGuid(),
            OwnerUserId = game.OwnerId,
            GameId = game.GameId,
            AttachmentKind = kind,
            Status = status,
            DeclaredContentType = "image/png",
            DeclaredBytes = 128,
            SourceContentType = hasSource ? "image/png" : null,
            SourceBytes = hasSource ? 128 : null,
            SourceETag = hasSource ? $"\"{Guid.NewGuid():N}\"" : null,
            DetectedContentType = ready ? "image/png" : null,
            PixelWidth = ready ? 80 : null,
            PixelHeight = ready ? 45 : null,
            FrameCount = ready ? 1 : null,
            IsCurrent = ready,
            SortOrder = sortOrder,
            PublishedAt = ready ? Now.AddMinutes(-5) : null,
            CreatedAt = Now.AddMinutes(-10),
            UpdatedAt = Now.AddMinutes(-5),
            StatusChangedAt = Now.AddMinutes(-5),
            UploadExpiresAt = Now.AddHours(23).AddMinutes(50),
        };
    }

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

    private sealed class RecordingObjectStorage : IMediaObjectStorage
    {
        public List<MediaObjectKey> RequestedKeys { get; } = [];

        public Task<Uri> CreateDownloadUrlAsync(
            ObjectStorageDownloadUrlRequest request,
            CancellationToken cancellationToken = default)
        {
            RequestedKeys.Add(request.ObjectKey);
            return Task.FromResult(new Uri(
                $"https://media.example.test/{request.ObjectKey.Value}"));
        }

        public Task<Uri> CreateUploadUrlAsync(
            ObjectStorageUploadUrlRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ObjectStorageMetadata?> GetMetadataAsync(
            MediaObjectKey objectKey,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ObjectStorageReadResult> OpenReadAsync(
            MediaObjectKey objectKey,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task WriteAsync(
            ObjectStorageWriteRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CopyAsync(
            ObjectStorageCopyRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteIfExistsAsync(
            MediaObjectKey objectKey,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
