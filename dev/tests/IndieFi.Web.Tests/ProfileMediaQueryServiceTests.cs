using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Domain;
using IndieFi.Web.Modules.Profiles.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class ProfileMediaQueryServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 17, 8, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlFixture _fixture;

    public ProfileMediaQueryServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetOwnerAsync_ReturnsVisibleStatesAndOnlyReadyThumbnails()
    {
        await ResetAsync();
        var userId = await CreateProfileAsync();
        var current = Media(userId, MediaStatus.Ready);
        var replacement = Media(userId, MediaStatus.ProcessingPending);
        var failed = Media(userId, MediaStatus.Failed);
        failed.FailureCode = "image_invalid_format";
        var deleting = Media(userId, MediaStatus.DeletePending);
        await AddMediaAsync(current, replacement, failed, deleting);
        var storage = new RecordingObjectStorage();

        await using var db = _fixture.NewDbContext();
        var service = new ProfileMediaQueryService(db, storage);
        var result = await service.GetOwnerAsync(
            userId,
            TestContext.Current.CancellationToken);
        var missing = await service.GetOwnerAsync(
            long.MaxValue,
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Null(missing);
        Assert.Equal(
            [current.Id, replacement.Id, failed.Id],
            result.Images.Select(item => item.MediaId));
        Assert.NotNull(result.Images[0].PreviewUrl);
        Assert.Null(result.Images[1].PreviewUrl);
        Assert.Null(result.Images[2].PreviewUrl);
        var requested = Assert.Single(storage.RequestedKeys);
        Assert.Equal(MediaDerivedVariant.ProfileThumbnail, requested.Variant);
    }

    [Fact]
    public async Task GetPublicAsync_ReturnsOnlyCurrentReadyDisplayImage()
    {
        await ResetAsync();
        var userId = await CreateProfileAsync();
        var current = Media(userId, MediaStatus.Ready);
        var replacement = Media(userId, MediaStatus.ProcessingPending);
        await AddMediaAsync(current, replacement);
        var storage = new RecordingObjectStorage();

        await using var db = _fixture.NewDbContext();
        var service = new ProfileMediaQueryService(db, storage);
        var result = await service.GetPublicAsync(
            userId,
            TestContext.Current.CancellationToken);
        var missing = await service.GetPublicAsync(
            long.MaxValue,
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(current.Id, result.Image?.MediaId);
        Assert.Null(missing);
        var requested = Assert.Single(storage.RequestedKeys);
        Assert.Equal(MediaDerivedVariant.ProfileDisplay, requested.Variant);
    }

    [Fact]
    public async Task Queries_ForExistingUserWithoutProfile_ReturnEmptyMedia()
    {
        await ResetAsync();
        long userId;
        await using (var db = _fixture.NewDbContext())
        {
            userId = (await ReviewTestData.CreateUserWithCreditAsync(
                db,
                balance: 0)).Id;
        }

        await using var queryDb = _fixture.NewDbContext();
        var service = new ProfileMediaQueryService(
            queryDb,
            new RecordingObjectStorage());

        var owner = await service.GetOwnerAsync(
            userId,
            TestContext.Current.CancellationToken);
        var published = await service.GetPublicAsync(
            userId,
            TestContext.Current.CancellationToken);

        Assert.NotNull(owner);
        Assert.Empty(owner.Images);
        Assert.NotNull(published);
        Assert.Null(published.Image);
    }

    private async Task<long> CreateProfileAsync()
    {
        await using var db = _fixture.NewDbContext();
        var user = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        db.UserProfiles.Add(new UserProfile
        {
            UserId = user.Id,
            Biography = string.Empty,
            CreatedAt = Now.AddDays(-1),
            UpdatedAt = Now.AddDays(-1),
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user.Id;
    }

    private async Task AddMediaAsync(params MediaAsset[] media)
    {
        await using var db = _fixture.NewDbContext();
        db.MediaAssets.AddRange(media);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static MediaAsset Media(long userId, MediaStatus status)
    {
        var ready = status == MediaStatus.Ready;
        var hasSource = status is MediaStatus.ProcessingPending
            or MediaStatus.Processing
            or MediaStatus.Ready;
        return new MediaAsset
        {
            Id = Guid.NewGuid(),
            OwnerUserId = userId,
            ProfileUserId = userId,
            AttachmentKind = MediaAttachmentKind.ProfileImage,
            Status = status,
            DeclaredContentType = "image/png",
            DeclaredBytes = 128,
            SourceContentType = hasSource ? "image/png" : null,
            SourceBytes = hasSource ? 128 : null,
            SourceETag = hasSource ? $"\"{Guid.NewGuid():N}\"" : null,
            DetectedContentType = ready ? "image/png" : null,
            PixelWidth = ready ? 80 : null,
            PixelHeight = ready ? 80 : null,
            FrameCount = ready ? 1 : null,
            IsCurrent = ready,
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
