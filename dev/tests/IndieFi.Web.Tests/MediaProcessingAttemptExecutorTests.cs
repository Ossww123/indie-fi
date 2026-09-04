using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Domain;
using IndieFi.Web.Modules.Media.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class MediaProcessingAttemptExecutorTests
{
    private readonly PostgreSqlFixture _fixture;

    public MediaProcessingAttemptExecutorTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Attempt_WritesBothGameDerivativesBeforePublishingReady()
    {
        var now = PostgreSqlTimestamp(DateTimeOffset.UtcNow.AddMinutes(-2));
        var source = EncodePng(width: 800, height: 450);
        var storage = new RecordingObjectStorage();
        var lease = await CreateMediaAsync(
            storage,
            source,
            now,
            MediaStatus.Processing);

        MediaProcessingAttemptResult result;
        await using (var db = _fixture.NewDbContext())
        {
            result = await NewAttemptExecutor(db, storage, new MutableTimeProvider(now))
                .ExecuteAsync(lease, TestContext.Current.CancellationToken);
        }

        Assert.Equal(MediaProcessingAttemptOutcome.Completed, result.Outcome);
        Assert.Null(result.FailureCode);
        AssertDerived(
            storage,
            MediaObjectKey.Derived(
                lease.MediaId,
                lease.Revision,
                MediaDerivedVariant.GameDisplay),
            width: 800,
            height: 450);
        AssertDerived(
            storage,
            MediaObjectKey.Derived(
                lease.MediaId,
                lease.Revision,
                MediaDerivedVariant.GameList),
            width: 640,
            height: 360);

        await using var verificationDb = _fixture.NewDbContext();
        var stored = await verificationDb.MediaAssets.AsNoTracking().SingleAsync(
            media => media.Id == lease.MediaId,
            TestContext.Current.CancellationToken);
        Assert.Equal(MediaStatus.Ready, stored.Status);
        Assert.True(stored.IsCurrent);
        Assert.Equal("image/png", stored.DetectedContentType);
        Assert.Equal(800, stored.PixelWidth);
        Assert.Equal(450, stored.PixelHeight);
        Assert.Equal(1, stored.FrameCount);
        Assert.Null(stored.ProcessingLeaseId);
        Assert.Null(stored.SourceDeletedAt);
        Assert.True(storage.Contains(MediaObjectKey.Source(lease.MediaId, lease.Revision)));
    }

    [Fact]
    public async Task PartialWriteFailure_RetryOverwritesSameBytesAndThenPublishes()
    {
        var now = PostgreSqlTimestamp(DateTimeOffset.UtcNow.AddMinutes(-2));
        var timeProvider = new MutableTimeProvider(now);
        var source = EncodePng(width: 800, height: 450);
        var storage = new RecordingObjectStorage { FailWriteNumberOnce = 2 };
        var pending = await CreateMediaAsync(
            storage,
            source,
            now,
            MediaStatus.ProcessingPending);

        await ExecuteWorkAsync(pending.MediaId, storage, timeProvider);

        await using (var firstDb = _fixture.NewDbContext())
        {
            var afterFailure = await firstDb.MediaAssets.AsNoTracking().SingleAsync(
                media => media.Id == pending.MediaId,
                TestContext.Current.CancellationToken);
            Assert.Equal(MediaStatus.ProcessingPending, afterFailure.Status);
            Assert.Equal(1, afterFailure.ProcessingAttemptCount);
            Assert.Equal(now.AddMinutes(1), afterFailure.NextProcessingAttemptAt);
        }

        timeProvider.UtcNow = now.AddMinutes(1);
        await ExecuteWorkAsync(pending.MediaId, storage, timeProvider);

        var displayKey = MediaObjectKey.Derived(
            pending.MediaId,
            pending.Revision,
            MediaDerivedVariant.GameDisplay);
        var writes = storage.WriteHistory[displayKey.Value];
        Assert.Equal(2, writes.Count);
        Assert.True(writes[0].AsSpan().SequenceEqual(writes[1]));
        await using var verificationDb = _fixture.NewDbContext();
        var stored = await verificationDb.MediaAssets.AsNoTracking().SingleAsync(
            media => media.Id == pending.MediaId,
            TestContext.Current.CancellationToken);
        Assert.Equal(MediaStatus.Ready, stored.Status);
        Assert.Equal(2, stored.ProcessingAttemptCount);
    }

    [Fact]
    public async Task MissingSource_IsAStablePermanentFailure()
    {
        var now = PostgreSqlTimestamp(DateTimeOffset.UtcNow.AddMinutes(-2));
        var storage = new RecordingObjectStorage();
        var source = EncodePng(width: 16, height: 9);
        var pending = await CreateMediaAsync(
            storage,
            source,
            now,
            MediaStatus.ProcessingPending);
        storage.Remove(MediaObjectKey.Source(pending.MediaId, pending.Revision));

        await ExecuteWorkAsync(
            pending.MediaId,
            storage,
            new MutableTimeProvider(now));

        await using var verificationDb = _fixture.NewDbContext();
        var stored = await verificationDb.MediaAssets.AsNoTracking().SingleAsync(
            media => media.Id == pending.MediaId,
            TestContext.Current.CancellationToken);
        Assert.Equal(MediaStatus.Failed, stored.Status);
        Assert.Equal(
            MediaProcessingAttemptExecutor.SourceObjectMissingFailureCode,
            stored.FailureCode);
        Assert.Empty(storage.WriteHistory);
    }

    private async Task ExecuteWorkAsync(
        Guid mediaId,
        RecordingObjectStorage storage,
        MutableTimeProvider timeProvider)
    {
        await using var db = _fixture.NewDbContext();
        var leases = new MediaProcessingLeaseService(db, timeProvider);
        var executor = new MediaProcessingWorkExecutor(
            leases,
            NewAttemptExecutor(db, storage, timeProvider),
            NullLogger<MediaProcessingWorkExecutor>.Instance);
        await executor.ExecuteAsync(
            mediaId,
            revision: 1,
            TestContext.Current.CancellationToken);
    }

    private MediaProcessingAttemptExecutor NewAttemptExecutor(
        AppDbContext db,
        RecordingObjectStorage storage,
        TimeProvider timeProvider) =>
        new(
            db,
            storage,
            new SkiaMediaImageProcessor(),
            new SkiaMediaImageDerivativeEncoder(),
            new MediaPublicationService(
                db,
                new NoOpDeletionPublisher(),
                timeProvider));

    private async Task<MediaProcessingLease> CreateMediaAsync(
        RecordingObjectStorage storage,
        byte[] source,
        DateTimeOffset now,
        MediaStatus status)
    {
        await using var db = _fixture.NewDbContext();
        var owner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var game = new Game
        {
            OwnerId = owner.Id,
            Title = $"Processing attempt {Guid.NewGuid():N}",
        };
        db.Games.Add(game);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var mediaId = Guid.NewGuid();
        var leaseId = status == MediaStatus.Processing ? Guid.NewGuid() : (Guid?)null;
        var etag = $"\"{Guid.NewGuid():N}\"";
        var media = new MediaAsset
        {
            Id = mediaId,
            OwnerUserId = owner.Id,
            GameId = game.Id,
            AttachmentKind = MediaAttachmentKind.GameScreenshot,
            Status = status,
            DeclaredContentType = "image/png",
            DeclaredBytes = source.Length,
            SourceContentType = "image/png",
            SourceBytes = source.Length,
            SourceETag = etag,
            ProcessingAttemptCount = status == MediaStatus.Processing ? 1 : 0,
            ProcessingLeaseId = leaseId,
            ProcessingLeaseExpiresAt = leaseId is not null ? now.AddMinutes(5) : null,
            CreatedAt = now.AddMinutes(-1),
            UpdatedAt = now,
            StatusChangedAt = now,
            UploadExpiresAt = now.AddHours(23).AddMinutes(59),
        };
        db.MediaAssets.Add(media);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        storage.Add(
            MediaObjectKey.Source(mediaId, revision: 1),
            source,
            "image/png",
            etag);
        return new MediaProcessingLease(
            mediaId,
            Revision: 1,
            leaseId ?? Guid.Empty,
            AttemptNumber: status == MediaStatus.Processing ? 1 : 0,
            ExpiresAt: media.ProcessingLeaseExpiresAt ?? now);
    }

    private static byte[] EncodePng(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.MediumPurple);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, quality: 100);
        return data.ToArray();
    }

    private static void AssertDerived(
        RecordingObjectStorage storage,
        MediaObjectKey key,
        int width,
        int height)
    {
        var bytes = storage.Get(key);
        using var stream = new MemoryStream(bytes, writable: false);
        using var codec = SKCodec.Create(stream);
        Assert.NotNull(codec);
        Assert.Equal(SKEncodedImageFormat.Webp, codec.EncodedFormat);
        Assert.Equal(width, codec.Info.Width);
        Assert.Equal(height, codec.Info.Height);
    }

    private static DateTimeOffset PostgreSqlTimestamp(DateTimeOffset value) =>
        value.AddTicks(-(value.Ticks % TimeSpan.TicksPerMicrosecond));

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class NoOpDeletionPublisher : IMediaDeletionPublisher
    {
        public Task StageAsync(
            Guid eventId,
            Guid mediaId,
            int revision,
            DateTimeOffset occurredAt,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingObjectStorage : IMediaObjectStorage
    {
        private readonly Dictionary<string, StoredObject> _objects = [];
        private int _writeNumber;

        public int? FailWriteNumberOnce { get; init; }
        public Dictionary<string, List<byte[]>> WriteHistory { get; } = [];

        public void Add(
            MediaObjectKey key,
            byte[] content,
            string contentType,
            string etag) =>
            _objects[key.Value] = new StoredObject(content, contentType, etag);

        public void Remove(MediaObjectKey key) => _objects.Remove(key.Value);
        public bool Contains(MediaObjectKey key) => _objects.ContainsKey(key.Value);
        public byte[] Get(MediaObjectKey key) => _objects[key.Value].Content;

        public Task<Uri> CreateUploadUrlAsync(
            ObjectStorageUploadUrlRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Uri> CreateDownloadUrlAsync(
            ObjectStorageDownloadUrlRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ObjectStorageMetadata?> GetMetadataAsync(
            MediaObjectKey objectKey,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_objects.TryGetValue(objectKey.Value, out var item)
                ? item.Metadata
                : null);
        }

        public Task<ObjectStorageReadResult> OpenReadAsync(
            MediaObjectKey objectKey,
            CancellationToken cancellationToken = default)
        {
            if (!_objects.TryGetValue(objectKey.Value, out var item))
            {
                throw new MediaObjectStorageException(
                    MediaObjectStorageFailureCode.ObjectNotFound,
                    "The test object does not exist.");
            }

            return Task.FromResult(new ObjectStorageReadResult(
                new MemoryStream(item.Content, writable: false),
                item.Metadata));
        }

        public async Task WriteAsync(
            ObjectStorageWriteRequest request,
            CancellationToken cancellationToken = default)
        {
            _writeNumber++;
            if (FailWriteNumberOnce == _writeNumber)
            {
                throw new IOException("Simulated partial derivative write failure.");
            }

            using var copy = new MemoryStream();
            await request.Content.CopyToAsync(copy, cancellationToken);
            var content = copy.ToArray();
            if (!WriteHistory.TryGetValue(request.ObjectKey.Value, out var writes))
            {
                writes = [];
                WriteHistory[request.ObjectKey.Value] = writes;
            }

            writes.Add(content);
            _objects[request.ObjectKey.Value] = new StoredObject(
                content,
                request.ContentType,
                $"\"{Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(content))}\"");
        }

        public Task CopyAsync(
            ObjectStorageCopyRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteIfExistsAsync(
            MediaObjectKey objectKey,
            CancellationToken cancellationToken = default)
        {
            _objects.Remove(objectKey.Value);
            return Task.CompletedTask;
        }

        private sealed record StoredObject(
            byte[] Content,
            string ContentType,
            string ETag)
        {
            public ObjectStorageMetadata Metadata => new(
                Content.Length,
                ETag,
                ContentType,
                DateTimeOffset.UtcNow);
        }
    }
}
