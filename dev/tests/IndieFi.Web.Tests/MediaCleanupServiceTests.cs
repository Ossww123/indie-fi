using IndieFi.Web.Data;
using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class MediaCleanupServiceTests
{
    private readonly PostgreSqlFixture _fixture;

    public MediaCleanupServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DeletionExecutor_RemovesEveryObjectBeforeWritingDeletedTombstone()
    {
        var now = new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var storage = new DeletionObjectStorage();
        var media = await CreateMediaAsync(
            MediaStatus.DeletePending,
            now,
            withUploadAttempt: true);
        AddAllObjects(storage, media);
        storage.Remove(MediaObjectKey.Derived(
            media.Id,
            media.Revision,
            MediaDerivedVariant.ProfileThumbnail));

        await using (var db = _fixture.NewDbContext())
        {
            var executor = NewDeletionExecutor(db, storage, now);
            await executor.ExecuteAsync(
                media.Id,
                media.Revision,
                TestContext.Current.CancellationToken);
            await executor.ExecuteAsync(
                media.Id,
                media.Revision,
                TestContext.Current.CancellationToken);
        }

        Assert.Empty(storage.Keys);
        await using var verificationDb = _fixture.NewDbContext();
        var stored = await verificationDb.MediaAssets.AsNoTracking().SingleAsync(
            item => item.Id == media.Id,
            TestContext.Current.CancellationToken);
        Assert.Equal(MediaStatus.Deleted, stored.Status);
        Assert.Equal(now, stored.SourceDeletedAt);
        Assert.Equal(now, stored.DeletionCompletedAt);
        Assert.Equal(now.AddDays(30), stored.TombstoneExpiresAt);
        Assert.Null(stored.SourceContentType);
        Assert.Null(stored.SourceBytes);
        Assert.Null(stored.SourceETag);
        Assert.Null(stored.DetectedContentType);
    }

    [Fact]
    public async Task DeletionFailure_LeavesDeletePendingUntilACompleteRetry()
    {
        var now = new DateTimeOffset(1900, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var storage = new DeletionObjectStorage { FailDeleteNumberOnce = 2 };
        var media = await CreateMediaAsync(
            MediaStatus.DeletePending,
            now,
            withUploadAttempt: true);
        AddAllObjects(storage, media);

        await using (var db = _fixture.NewDbContext())
        {
            await Assert.ThrowsAsync<MediaObjectStorageException>(() =>
                NewDeletionExecutor(db, storage, now).ExecuteAsync(
                    media.Id,
                    media.Revision,
                    TestContext.Current.CancellationToken));
        }

        await using (var failedDb = _fixture.NewDbContext())
        {
            Assert.Equal(
                MediaStatus.DeletePending,
                await failedDb.MediaAssets
                    .Where(item => item.Id == media.Id)
                    .Select(item => item.Status)
                    .SingleAsync(TestContext.Current.CancellationToken));
        }

        await using (var retryDb = _fixture.NewDbContext())
        {
            await NewDeletionExecutor(retryDb, storage, now.AddSeconds(1))
                .ExecuteAsync(
                    media.Id,
                    media.Revision,
                    TestContext.Current.CancellationToken);
        }

        Assert.Empty(storage.Keys);
        await using var verificationDb = _fixture.NewDbContext();
        Assert.Equal(
            MediaStatus.Deleted,
            await verificationDb.MediaAssets
                .Where(item => item.Id == media.Id)
                .Select(item => item.Status)
                .SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CleanupBatch_ConvergesExpiredReadyFailedDeleteAndDueTombstone()
    {
        var now = new DateTimeOffset(1900, 1, 3, 0, 0, 0, TimeSpan.Zero);
        var storage = new DeletionObjectStorage();
        var expiredUpload = await CreateMediaAsync(
            MediaStatus.UploadPending,
            now,
            uploadExpired: true,
            withUploadAttempt: true);
        var ready = await CreateMediaAsync(
            MediaStatus.Ready,
            now,
            withUploadAttempt: true);
        var failed = await CreateMediaAsync(
            MediaStatus.Failed,
            now,
            withUploadAttempt: true);
        var deletePending = await CreateMediaAsync(
            MediaStatus.DeletePending,
            now,
            withUploadAttempt: true);
        var dueTombstone = await CreateDueTombstoneAsync(now);
        AddAllObjects(storage, expiredUpload);
        AddAllObjects(storage, ready);
        AddAllObjects(storage, failed);
        AddAllObjects(storage, deletePending);

        MediaCleanupBatchResult result;
        await using (var db = _fixture.NewDbContext())
        {
            var objectDeletion = new MediaObjectDeletionService(storage);
            result = await new MediaCleanupService(
                    db,
                    objectDeletion,
                    new MediaDeletionWorkExecutor(
                        db,
                        objectDeletion,
                        new MutableTimeProvider(now)),
                    new MutableTimeProvider(now),
                    NullLogger<MediaCleanupService>.Instance)
                .RunBatchAsync(
                    batchSize: 10,
                    TestContext.Current.CancellationToken);
        }

        Assert.Equal(5, result.ExaminedCount);
        Assert.Equal(1, result.ExpiredCount);
        Assert.Equal(1, result.ReadySourceDeletedCount);
        Assert.Equal(2, result.DeletionCompletedCount);
        Assert.Equal(1, result.TombstoneRemovedCount);
        Assert.Equal(0, result.FailedCount);

        await using var verificationDb = _fixture.NewDbContext();
        var stored = await verificationDb.MediaAssets.AsNoTracking()
            .Where(item => item.Id == expiredUpload.Id
                || item.Id == ready.Id
                || item.Id == failed.Id
                || item.Id == deletePending.Id)
            .ToDictionaryAsync(
                item => item.Id,
                TestContext.Current.CancellationToken);
        Assert.Equal(MediaStatus.Expired, stored[expiredUpload.Id].Status);
        Assert.NotNull(stored[expiredUpload.Id].DeletionCompletedAt);
        Assert.Equal(MediaStatus.Ready, stored[ready.Id].Status);
        Assert.Equal(now, stored[ready.Id].SourceDeletedAt);
        Assert.Null(stored[ready.Id].DeletionCompletedAt);
        Assert.Equal(MediaStatus.Failed, stored[failed.Id].Status);
        Assert.Equal("image_invalid_format", stored[failed.Id].FailureCode);
        Assert.NotNull(stored[failed.Id].DeletionCompletedAt);
        Assert.Equal(MediaStatus.Deleted, stored[deletePending.Id].Status);
        Assert.False(await verificationDb.MediaAssets.AnyAsync(
            item => item.Id == dueTombstone.Id,
            TestContext.Current.CancellationToken));

        Assert.False(storage.ContainsSource(ready));
        Assert.True(storage.ContainsDerived(ready));
        Assert.DoesNotContain(
            storage.Keys,
            key => key.Contains(expiredUpload.Id.ToString("N"), StringComparison.Ordinal)
                || key.Contains(failed.Id.ToString("N"), StringComparison.Ordinal)
                || key.Contains(deletePending.Id.ToString("N"), StringComparison.Ordinal));
    }

    private async Task<MediaAsset> CreateMediaAsync(
        MediaStatus status,
        DateTimeOffset now,
        bool uploadExpired = false,
        bool withUploadAttempt = false,
        DateTimeOffset? createdAtOverride = null)
    {
        await using var db = _fixture.NewDbContext();
        var owner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var game = await ReviewTestData.CreateGameAsync(db, owner.Id);
        var createdAt = createdAtOverride ?? (uploadExpired
            ? now.AddHours(-25)
            : now.AddHours(-1));
        var hasSource = status is
            MediaStatus.ProcessingPending or
            MediaStatus.Processing or
            MediaStatus.Ready or
            MediaStatus.Failed or
            MediaStatus.DeletePending or
            MediaStatus.Deleted;
        var hasValidation = status is
            MediaStatus.Ready or MediaStatus.DeletePending;
        var media = new MediaAsset
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner.Id,
            GameId = game.Id,
            AttachmentKind = MediaAttachmentKind.GameScreenshot,
            Status = status,
            DeclaredContentType = "image/png",
            DeclaredBytes = 2048,
            SourceContentType = hasSource ? "image/png" : null,
            SourceBytes = hasSource ? 2048 : null,
            SourceETag = hasSource ? $"\"{Guid.NewGuid():N}\"" : null,
            DetectedContentType = hasValidation ? "image/png" : null,
            PixelWidth = hasValidation ? 800 : null,
            PixelHeight = hasValidation ? 450 : null,
            FrameCount = hasValidation ? 1 : null,
            FailureCode = status == MediaStatus.Failed
                ? "image_invalid_format"
                : null,
            IsCurrent = status == MediaStatus.Ready,
            SortOrder = status == MediaStatus.Ready ? 0 : null,
            PublishedAt = status == MediaStatus.Ready ? createdAt.AddMinutes(1) : null,
            CreatedAt = createdAt,
            UpdatedAt = now.AddMinutes(-1),
            StatusChangedAt = now.AddMinutes(-1),
            UploadExpiresAt = createdAt.AddHours(24),
        };
        if (withUploadAttempt)
        {
            media.UploadAttempts.Add(new MediaUploadAttempt
            {
                Id = Guid.NewGuid(),
                IssuedAt = createdAt,
                UrlExpiresAt = createdAt.AddMinutes(15),
            });
        }

        db.MediaAssets.Add(media);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return media;
    }

    private async Task<MediaAsset> CreateDueTombstoneAsync(DateTimeOffset now)
    {
        var media = await CreateMediaAsync(
            MediaStatus.Expired,
            now,
            createdAtOverride: now.AddDays(-62));
        await using var db = _fixture.NewDbContext();
        var stored = await db.MediaAssets.SingleAsync(
            item => item.Id == media.Id,
            TestContext.Current.CancellationToken);
        var completedAt = now.AddDays(-31);
        stored.SourceDeletedAt = completedAt;
        stored.DeletionCompletedAt = completedAt;
        stored.TombstoneExpiresAt = completedAt.AddDays(30);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return media;
    }

    private static MediaDeletionWorkExecutor NewDeletionExecutor(
        AppDbContext db,
        DeletionObjectStorage storage,
        DateTimeOffset now) =>
        new(
            db,
            new MediaObjectDeletionService(storage),
            new MutableTimeProvider(now));

    private static void AddAllObjects(
        DeletionObjectStorage storage,
        MediaAsset media)
    {
        foreach (var attempt in media.UploadAttempts)
        {
            storage.Add(MediaObjectKey.Staging(media.Id, attempt.Id));
        }

        storage.Add(MediaObjectKey.Source(media.Id, media.Revision));
        foreach (var variant in Enum.GetValues<MediaDerivedVariant>())
        {
            storage.Add(MediaObjectKey.Derived(media.Id, media.Revision, variant));
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class DeletionObjectStorage : IMediaObjectStorage
    {
        private readonly HashSet<string> _keys = [];
        private int _deleteNumber;

        public int? FailDeleteNumberOnce { get; init; }
        public IReadOnlyCollection<string> Keys => _keys;

        public void Add(MediaObjectKey key) => _keys.Add(key.Value);
        public void Remove(MediaObjectKey key) => _keys.Remove(key.Value);
        public bool ContainsSource(MediaAsset media) =>
            _keys.Contains(MediaObjectKey.Source(media.Id, media.Revision).Value);
        public bool ContainsDerived(MediaAsset media) =>
            Enum.GetValues<MediaDerivedVariant>().Any(variant =>
                _keys.Contains(MediaObjectKey.Derived(
                    media.Id,
                    media.Revision,
                    variant).Value));

        public Task DeleteIfExistsAsync(
            MediaObjectKey objectKey,
            CancellationToken cancellationToken = default)
        {
            _deleteNumber++;
            if (FailDeleteNumberOnce == _deleteNumber)
            {
                throw new MediaObjectStorageException(
                    MediaObjectStorageFailureCode.Unavailable,
                    "Simulated delete failure.");
            }

            _keys.Remove(objectKey.Value);
            return Task.CompletedTask;
        }

        public Task<Uri> CreateUploadUrlAsync(ObjectStorageUploadUrlRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Uri> CreateDownloadUrlAsync(ObjectStorageDownloadUrlRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ObjectStorageMetadata?> GetMetadataAsync(MediaObjectKey objectKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ObjectStorageReadResult> OpenReadAsync(MediaObjectKey objectKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task WriteAsync(ObjectStorageWriteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CopyAsync(ObjectStorageCopyRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
