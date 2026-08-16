using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Contracts;
using IndieFi.Web.Modules.Media.Domain;
using IndieFi.Web.Modules.Media.Infrastructure;
using IndieFi.Web.Modules.Notifications;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class MediaUploadCompletionServiceTests
{
    private readonly PostgreSqlFixture _fixture;

    public MediaUploadCompletionServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CompleteAsync_WithMatchingUpload_FixesSourceAndIsIdempotent()
    {
        var pending = await CreatePendingUploadAsync();
        var storage = NewStorageWithStaging(pending);
        var publisher = new RecordingProcessingPublisher();

        MediaUploadCompletionResult first;
        await using (var firstDb = _fixture.NewDbContext())
        {
            first = await NewService(
                firstDb,
                storage,
                publisher,
                pending.CompletionAt).CompleteAsync(
                    pending.OwnerId,
                    pending.MediaId,
                    pending.UploadAttemptId,
                    TestContext.Current.CancellationToken);
        }

        MediaUploadCompletionResult repeated;
        await using (var repeatedDb = _fixture.NewDbContext())
        {
            repeated = await NewService(
                repeatedDb,
                storage,
                publisher,
                pending.CompletionAt.AddSeconds(1)).CompleteAsync(
                    pending.OwnerId,
                    pending.MediaId,
                    pending.UploadAttemptId,
                    TestContext.Current.CancellationToken);
        }

        var expectedEventId = MediaProcessingEventId.Create(
            pending.MediaId,
            revision: 1);
        Assert.Equal(MediaUploadCompletionOutcome.Completed, first.Outcome);
        Assert.Equal(expectedEventId, first.ProcessingEventId);
        Assert.Equal(MediaUploadCompletionOutcome.AlreadyCompleted, repeated.Outcome);
        Assert.Equal(expectedEventId, repeated.ProcessingEventId);
        var copy = Assert.Single(storage.CopyRequests);
        Assert.Equal(
            MediaObjectKey.Staging(pending.MediaId, pending.UploadAttemptId),
            copy.SourceObjectKey);
        Assert.Equal(MediaObjectKey.Source(pending.MediaId, 1), copy.DestinationObjectKey);
        Assert.Equal(pending.StagingETag, copy.SourceETag);
        Assert.Equal(pending.ContentType, copy.ContentType);
        var staged = Assert.Single(publisher.Messages);
        Assert.Equal(expectedEventId, staged.EventId);
        Assert.Equal(pending.MediaId, staged.MediaId);
        Assert.Equal(1, staged.Revision);

        await using var verificationDb = _fixture.NewDbContext();
        var media = await verificationDb.MediaAssets
            .AsNoTracking()
            .SingleAsync(
                item => item.Id == pending.MediaId,
                TestContext.Current.CancellationToken);
        var attempt = await verificationDb.MediaUploadAttempts
            .AsNoTracking()
            .SingleAsync(
                item => item.Id == pending.UploadAttemptId,
                TestContext.Current.CancellationToken);
        Assert.Equal(MediaStatus.ProcessingPending, media.Status);
        Assert.Equal(pending.ContentType, media.SourceContentType);
        Assert.Equal(pending.DeclaredBytes, media.SourceBytes);
        Assert.Equal(storage.SourceETag(pending.MediaId), media.SourceETag);
        Assert.Equal(pending.CompletionAt, attempt.CompletedAt);
    }

    [Theory]
    [InlineData(2049, "image/png")]
    [InlineData(2048, "image/jpeg")]
    [InlineData(0, "image/png")]
    public async Task CompleteAsync_WithMismatchedMetadata_RejectsWithoutCopyOrProcessing(
        long actualBytes,
        string actualContentType)
    {
        var pending = await CreatePendingUploadAsync();
        var storage = new RecordingMediaObjectStorage();
        storage.SetMetadata(
            MediaObjectKey.Staging(pending.MediaId, pending.UploadAttemptId),
            new ObjectStorageMetadata(
                actualBytes,
                pending.StagingETag,
                actualContentType,
                pending.CompletionAt));
        var publisher = new RecordingProcessingPublisher();

        MediaUploadCompletionResult first;
        await using (var db = _fixture.NewDbContext())
        {
            first = await NewService(
                db,
                storage,
                publisher,
                pending.CompletionAt).CompleteAsync(
                    pending.OwnerId,
                    pending.MediaId,
                    pending.UploadAttemptId,
                    TestContext.Current.CancellationToken);
        }

        MediaUploadCompletionResult repeated;
        await using (var repeatedDb = _fixture.NewDbContext())
        {
            repeated = await NewService(
                repeatedDb,
                storage,
                publisher,
                pending.CompletionAt.AddSeconds(1)).CompleteAsync(
                    pending.OwnerId,
                    pending.MediaId,
                    pending.UploadAttemptId,
                    TestContext.Current.CancellationToken);
        }

        Assert.Equal(MediaUploadCompletionOutcome.UploadRejected, first.Outcome);
        Assert.Equal(MediaUploadCompletionOutcome.UploadRejected, repeated.Outcome);
        Assert.Empty(storage.CopyRequests);
        Assert.Empty(publisher.Messages);

        await using var verificationDb = _fixture.NewDbContext();
        var media = await verificationDb.MediaAssets
            .AsNoTracking()
            .SingleAsync(
                item => item.Id == pending.MediaId,
                TestContext.Current.CancellationToken);
        var attempt = await verificationDb.MediaUploadAttempts
            .AsNoTracking()
            .SingleAsync(
                item => item.Id == pending.UploadAttemptId,
                TestContext.Current.CancellationToken);
        Assert.Equal(MediaStatus.Failed, media.Status);
        Assert.Equal(
            MediaUploadCompletionService.UploadMetadataMismatchFailureCode,
            media.FailureCode);
        Assert.Null(media.SourceETag);
        Assert.Equal(pending.CompletionAt, attempt.CompletedAt);
    }

    [Fact]
    public async Task CompleteAsync_WhenStagingObjectIsMissing_KeepsReservationPending()
    {
        var pending = await CreatePendingUploadAsync();
        var storage = new RecordingMediaObjectStorage();
        var publisher = new RecordingProcessingPublisher();

        await using var db = _fixture.NewDbContext();
        var result = await NewService(
            db,
            storage,
            publisher,
            pending.CompletionAt).CompleteAsync(
                pending.OwnerId,
                pending.MediaId,
                pending.UploadAttemptId,
                TestContext.Current.CancellationToken);

        Assert.Equal(
            MediaUploadCompletionOutcome.StagingObjectNotFound,
            result.Outcome);
        Assert.Equal(
            MediaStatus.UploadPending,
            await db.MediaAssets
                .Where(item => item.Id == pending.MediaId)
                .Select(item => item.Status)
                .SingleAsync(TestContext.Current.CancellationToken));
        Assert.Empty(storage.CopyRequests);
        Assert.Empty(publisher.Messages);
    }

    [Fact]
    public async Task CompleteAsync_WithInvalidStorageETag_FailsClosedWithoutMutation()
    {
        var pending = await CreatePendingUploadAsync();
        var storage = new RecordingMediaObjectStorage();
        storage.SetMetadata(
            MediaObjectKey.Staging(pending.MediaId, pending.UploadAttemptId),
            new ObjectStorageMetadata(
                pending.DeclaredBytes,
                "invalid-etag",
                pending.ContentType,
                pending.CompletionAt));

        await using var db = _fixture.NewDbContext();
        var result = await NewService(
            db,
            storage,
            new RecordingProcessingPublisher(),
            pending.CompletionAt).CompleteAsync(
                pending.OwnerId,
                pending.MediaId,
                pending.UploadAttemptId,
                TestContext.Current.CancellationToken);

        Assert.Equal(
            MediaUploadCompletionOutcome.StorageResponseInvalid,
            result.Outcome);
        Assert.Empty(storage.CopyRequests);
        Assert.Equal(
            MediaStatus.UploadPending,
            await db.MediaAssets
                .Where(item => item.Id == pending.MediaId)
                .Select(item => item.Status)
                .SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompleteAsync_AfterCopyThenDatabaseFailure_UsesFirstSourceOnRetry()
    {
        var pending = await CreatePendingUploadAsync();
        var storage = NewStorageWithStaging(pending);
        var failingPublisher = new RecordingProcessingPublisher
        {
            ThrowOnStage = true,
        };

        await using (var failingDb = _fixture.NewDbContext())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                NewService(
                    failingDb,
                    storage,
                    failingPublisher,
                    pending.CompletionAt).CompleteAsync(
                        pending.OwnerId,
                        pending.MediaId,
                        pending.UploadAttemptId,
                        TestContext.Current.CancellationToken));
        }

        var sourceKey = MediaObjectKey.Source(pending.MediaId, 1);
        var firstSource = await storage.GetMetadataAsync(
            sourceKey,
            TestContext.Current.CancellationToken);
        Assert.NotNull(firstSource);
        Assert.Single(storage.CopyRequests);

        // The still-valid staging URL may overwrite its temporary key after the
        // first copy. Recovery must keep the already fixed source object.
        storage.SetMetadata(
            MediaObjectKey.Staging(pending.MediaId, pending.UploadAttemptId),
            new ObjectStorageMetadata(
                pending.DeclaredBytes,
                "\"later-staging-etag\"",
                pending.ContentType,
                pending.CompletionAt.AddSeconds(1)));

        var recoveringPublisher = new RecordingProcessingPublisher();
        MediaUploadCompletionResult recovered;
        await using (var recoveryDb = _fixture.NewDbContext())
        {
            recovered = await NewService(
                recoveryDb,
                storage,
                recoveringPublisher,
                pending.CompletionAt.AddSeconds(2)).CompleteAsync(
                    pending.OwnerId,
                    pending.MediaId,
                    pending.UploadAttemptId,
                    TestContext.Current.CancellationToken);
        }

        Assert.Equal(MediaUploadCompletionOutcome.Completed, recovered.Outcome);
        Assert.Single(storage.CopyRequests);
        Assert.Equal(
            firstSource,
            await storage.GetMetadataAsync(
                sourceKey,
                TestContext.Current.CancellationToken));
        Assert.Single(recoveringPublisher.Messages);
    }

    [Fact]
    public async Task CompleteAsync_WithConflictingExistingSource_FailsClosed()
    {
        var pending = await CreatePendingUploadAsync();
        var storage = NewStorageWithStaging(pending);
        storage.SetMetadata(
            MediaObjectKey.Source(pending.MediaId, 1),
            new ObjectStorageMetadata(
                pending.DeclaredBytes + 1,
                "\"conflicting-source\"",
                pending.ContentType,
                pending.CompletionAt));
        var publisher = new RecordingProcessingPublisher();

        await using var db = _fixture.NewDbContext();
        var result = await NewService(
            db,
            storage,
            publisher,
            pending.CompletionAt).CompleteAsync(
                pending.OwnerId,
                pending.MediaId,
                pending.UploadAttemptId,
                TestContext.Current.CancellationToken);

        Assert.Equal(
            MediaUploadCompletionOutcome.SourceObjectConflict,
            result.Outcome);
        Assert.Empty(storage.CopyRequests);
        Assert.Empty(publisher.Messages);
        Assert.Null(await db.MediaUploadAttempts
            .Where(item => item.Id == pending.UploadAttemptId)
            .Select(item => item.CompletedAt)
            .SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompleteAsync_WithSupersededAttempt_DoesNotReadStorage()
    {
        var pending = await CreatePendingUploadAsync();
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var attempt = await arrangeDb.MediaUploadAttempts.SingleAsync(
                item => item.Id == pending.UploadAttemptId,
                TestContext.Current.CancellationToken);
            attempt.SupersededAt = pending.CompletionAt;
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var storage = NewStorageWithStaging(pending);
        await using var db = _fixture.NewDbContext();
        var result = await NewService(
            db,
            storage,
            new RecordingProcessingPublisher(),
            pending.CompletionAt.AddSeconds(1)).CompleteAsync(
                pending.OwnerId,
                pending.MediaId,
                pending.UploadAttemptId,
                TestContext.Current.CancellationToken);

        Assert.Equal(
            MediaUploadCompletionOutcome.UploadAttemptSuperseded,
            result.Outcome);
        Assert.Empty(storage.MetadataReads);
        Assert.Empty(storage.CopyRequests);
    }

    [Fact]
    public async Task CompleteAsync_ForForeignOwner_HidesMediaAndDoesNotReadStorage()
    {
        var pending = await CreatePendingUploadAsync();
        long otherOwnerId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            otherOwnerId = (await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0)).Id;
        }

        var storage = NewStorageWithStaging(pending);
        await using var db = _fixture.NewDbContext();
        var result = await NewService(
            db,
            storage,
            new RecordingProcessingPublisher(),
            pending.CompletionAt).CompleteAsync(
                otherOwnerId,
                pending.MediaId,
                pending.UploadAttemptId,
                TestContext.Current.CancellationToken);

        Assert.Equal(MediaUploadCompletionOutcome.MediaNotFound, result.Outcome);
        Assert.Empty(storage.MetadataReads);
    }

    [Fact]
    public async Task CompleteAsync_WithMassTransitPublisher_StoresMinimalOutboxAtomically()
    {
        var pending = await CreatePendingUploadAsync();
        var storage = NewStorageWithStaging(pending);
        await using var provider = BuildOutboxServices(storage, pending.CompletionAt);

        MediaUploadCompletionResult result;
        await using (var scope = provider.CreateAsyncScope())
        {
            result = await scope.ServiceProvider
                .GetRequiredService<MediaUploadCompletionService>()
                .CompleteAsync(
                    pending.OwnerId,
                    pending.MediaId,
                    pending.UploadAttemptId,
                    TestContext.Current.CancellationToken);
        }

        var eventId = MediaProcessingEventId.Create(pending.MediaId, revision: 1);
        Assert.Equal(MediaUploadCompletionOutcome.Completed, result.Outcome);
        Assert.Equal(eventId, result.ProcessingEventId);

        await using var verificationDb = _fixture.NewDbContext();
        Assert.Equal(
            MediaStatus.ProcessingPending,
            await verificationDb.MediaAssets
                .Where(item => item.Id == pending.MediaId)
                .Select(item => item.Status)
                .SingleAsync(TestContext.Current.CancellationToken));
        var outbox = await verificationDb.Set<OutboxMessage>().SingleAsync(
            item => item.MessageId == eventId,
            TestContext.Current.CancellationToken);
        Assert.Contains(nameof(MediaProcessingRequestedV1), outbox.MessageType);
        Assert.Contains(pending.MediaId.ToString(), outbox.Body);
        Assert.DoesNotContain("staging/", outbox.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sources/", outbox.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(pending.StagingETag, outbox.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsync_CallerRollback_RemovesStateAndOutboxThenRecoversSource()
    {
        var pending = await CreatePendingUploadAsync();
        var storage = NewStorageWithStaging(pending);
        await using var provider = BuildOutboxServices(storage, pending.CompletionAt);
        var eventId = MediaProcessingEventId.Create(pending.MediaId, revision: 1);

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(
                TestContext.Current.CancellationToken);
            var result = await scope.ServiceProvider
                .GetRequiredService<MediaUploadCompletionService>()
                .CompleteAsync(
                    pending.OwnerId,
                    pending.MediaId,
                    pending.UploadAttemptId,
                    TestContext.Current.CancellationToken);

            Assert.Equal(MediaUploadCompletionOutcome.Completed, result.Outcome);
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        await using (var rolledBackDb = _fixture.NewDbContext())
        {
            Assert.Equal(
                MediaStatus.UploadPending,
                await rolledBackDb.MediaAssets
                    .Where(item => item.Id == pending.MediaId)
                    .Select(item => item.Status)
                    .SingleAsync(TestContext.Current.CancellationToken));
            Assert.Null(await rolledBackDb.MediaUploadAttempts
                .Where(item => item.Id == pending.UploadAttemptId)
                .Select(item => item.CompletedAt)
                .SingleAsync(TestContext.Current.CancellationToken));
            Assert.False(await rolledBackDb.Set<OutboxMessage>().AnyAsync(
                item => item.MessageId == eventId,
                TestContext.Current.CancellationToken));
        }

        Assert.NotNull(await storage.GetMetadataAsync(
            MediaObjectKey.Source(pending.MediaId, 1),
            TestContext.Current.CancellationToken));
        Assert.Single(storage.CopyRequests);

        await using (var recoveryScope = provider.CreateAsyncScope())
        {
            var recovered = await recoveryScope.ServiceProvider
                .GetRequiredService<MediaUploadCompletionService>()
                .CompleteAsync(
                    pending.OwnerId,
                    pending.MediaId,
                    pending.UploadAttemptId,
                    TestContext.Current.CancellationToken);
            Assert.Equal(MediaUploadCompletionOutcome.Completed, recovered.Outcome);
        }

        Assert.Single(storage.CopyRequests);
        await using var verificationDb = _fixture.NewDbContext();
        Assert.Equal(
            MediaStatus.ProcessingPending,
            await verificationDb.MediaAssets
                .Where(item => item.Id == pending.MediaId)
                .Select(item => item.Status)
                .SingleAsync(TestContext.Current.CancellationToken));
        Assert.True(await verificationDb.Set<OutboxMessage>().AnyAsync(
            item => item.MessageId == eventId,
            TestContext.Current.CancellationToken));
    }

    private async Task<PendingUpload> CreatePendingUploadAsync()
    {
        await using var db = _fixture.NewDbContext();
        var owner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var game = new Game
        {
            OwnerId = owner.Id,
            Title = $"Upload completion game {Guid.NewGuid():N}",
        };
        db.Games.Add(game);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var media = new MediaAsset
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner.Id,
            GameId = game.Id,
            AttachmentKind = MediaAttachmentKind.GameScreenshot,
            DeclaredContentType = "image/png",
            DeclaredBytes = 2048,
        };
        db.MediaAssets.Add(media);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var issuedAt = PostgreSqlTimestamp(DateTimeOffset.UtcNow);
        var attempt = new MediaUploadAttempt
        {
            Id = Guid.NewGuid(),
            MediaId = media.Id,
            IssuedAt = issuedAt,
            UrlExpiresAt = issuedAt.AddMinutes(15),
        };
        db.MediaUploadAttempts.Add(attempt);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var completionAt = PostgreSqlTimestamp(
            new[] { media.CreatedAt, issuedAt }.Max().AddMinutes(1));
        return new PendingUpload(
            owner.Id,
            media.Id,
            attempt.Id,
            media.DeclaredContentType,
            media.DeclaredBytes,
            "\"staging-etag\"",
            completionAt);
    }

    private static RecordingMediaObjectStorage NewStorageWithStaging(
        PendingUpload pending)
    {
        var storage = new RecordingMediaObjectStorage();
        storage.SetMetadata(
            MediaObjectKey.Staging(pending.MediaId, pending.UploadAttemptId),
            new ObjectStorageMetadata(
                pending.DeclaredBytes,
                pending.StagingETag,
                pending.ContentType,
                pending.CompletionAt));
        return storage;
    }

    private static MediaUploadCompletionService NewService(
        AppDbContext db,
        RecordingMediaObjectStorage storage,
        RecordingProcessingPublisher publisher,
        DateTimeOffset now) =>
        new(db, storage, publisher, new FixedTimeProvider(now));

    private ServiceProvider BuildOutboxServices(
        RecordingMediaObjectStorage storage,
        DateTimeOffset now)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(_fixture.ConnectionString));
        services.AddNotificationsModule(new ConfigurationBuilder().Build());
        services.AddSingleton<IMediaObjectStorage>(storage);
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(now));
        services.AddScoped<
            IMediaProcessingPublisher,
            MassTransitMediaProcessingPublisher>();
        services.AddScoped<MediaUploadCompletionService>();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static DateTimeOffset PostgreSqlTimestamp(DateTimeOffset value) =>
        value.AddTicks(-(value.Ticks % TimeSpan.TicksPerMicrosecond));

    private sealed record PendingUpload(
        long OwnerId,
        Guid MediaId,
        Guid UploadAttemptId,
        string ContentType,
        long DeclaredBytes,
        string StagingETag,
        DateTimeOffset CompletionAt);

    private sealed record StagedProcessingMessage(
        Guid EventId,
        Guid MediaId,
        int Revision,
        DateTimeOffset OccurredAt);

    private sealed class RecordingProcessingPublisher : IMediaProcessingPublisher
    {
        public List<StagedProcessingMessage> Messages { get; } = [];

        public bool ThrowOnStage { get; set; }

        public Task StageAsync(
            Guid eventId,
            Guid mediaId,
            int revision,
            DateTimeOffset occurredAt,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnStage)
            {
                throw new InvalidOperationException("Outbox staging failed for test.");
            }

            Messages.Add(new StagedProcessingMessage(
                eventId,
                mediaId,
                revision,
                occurredAt));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingMediaObjectStorage : IMediaObjectStorage
    {
        private readonly Dictionary<string, ObjectStorageMetadata> _metadata =
            new(StringComparer.Ordinal);

        public List<MediaObjectKey> MetadataReads { get; } = [];

        public List<ObjectStorageCopyRequest> CopyRequests { get; } = [];

        public void SetMetadata(
            MediaObjectKey key,
            ObjectStorageMetadata metadata)
        {
            _metadata[key.Value] = metadata;
        }

        public string SourceETag(Guid mediaId) =>
            _metadata[MediaObjectKey.Source(mediaId, 1).Value].ETag;

        public Task<ObjectStorageMetadata?> GetMetadataAsync(
            MediaObjectKey objectKey,
            CancellationToken cancellationToken = default)
        {
            MetadataReads.Add(objectKey);
            return Task.FromResult(
                _metadata.TryGetValue(objectKey.Value, out var metadata)
                    ? metadata
                    : null);
        }

        public Task CopyAsync(
            ObjectStorageCopyRequest request,
            CancellationToken cancellationToken = default)
        {
            CopyRequests.Add(request);
            var source = _metadata[request.SourceObjectKey.Value];
            if (!string.Equals(source.ETag, request.SourceETag, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The fake source changed before its conditional copy.");
            }

            _metadata[request.DestinationObjectKey.Value] =
                new ObjectStorageMetadata(
                    source.ContentLength,
                    $"\"source-{request.SourceETag.Trim('\"')}\"",
                    request.ContentType,
                    source.LastModified);
            return Task.CompletedTask;
        }

        public Task<Uri> CreateUploadUrlAsync(
            ObjectStorageUploadUrlRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Uri> CreateDownloadUrlAsync(
            ObjectStorageDownloadUrlRequest request,
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

        public Task DeleteIfExistsAsync(
            MediaObjectKey objectKey,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
