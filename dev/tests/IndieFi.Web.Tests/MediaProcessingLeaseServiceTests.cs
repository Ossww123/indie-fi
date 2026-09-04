using IndieFi.Web.Data;
using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class MediaProcessingLeaseServiceTests
{
    private readonly PostgreSqlFixture _fixture;

    public MediaProcessingLeaseServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ConcurrentAcquisition_GrantsExactlyOneTenMinuteLease()
    {
        var now = new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
        var mediaId = await CreateMediaAsync(MediaStatus.ProcessingPending, now);

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            await using var db = _fixture.NewDbContext();
            return await new MediaProcessingLeaseService(
                    db,
                    new MutableTimeProvider(now))
                .TryAcquireAsync(
                    mediaId,
                    revision: 1,
                    TestContext.Current.CancellationToken);
        }));

        var acquired = Assert.Single(
            results,
            result => result.Outcome
                == MediaProcessingLeaseAcquisitionOutcome.Acquired);
        Assert.NotNull(acquired.Lease);
        Assert.NotEqual(Guid.Empty, acquired.Lease.LeaseId);
        Assert.Equal(1, acquired.Lease.AttemptNumber);
        Assert.Equal(
            now + MediaLifecyclePolicy.ProcessingLeaseLifetime,
            acquired.Lease.ExpiresAt);
        Assert.All(
            results.Where(result => result != acquired),
            result => Assert.Equal(
                MediaProcessingLeaseAcquisitionOutcome.ActiveLease,
                result.Outcome));

        await using var verificationDb = _fixture.NewDbContext();
        var stored = await verificationDb.MediaAssets
            .AsNoTracking()
            .SingleAsync(
                media => media.Id == mediaId,
                TestContext.Current.CancellationToken);
        Assert.Equal(MediaStatus.Processing, stored.Status);
        Assert.Equal(1, stored.ProcessingAttemptCount);
        Assert.Equal(acquired.Lease.LeaseId, stored.ProcessingLeaseId);
    }

    [Fact]
    public async Task ExpiredLease_IsReclaimedButActiveLeaseIsNot()
    {
        var now = new DateTimeOffset(2026, 8, 16, 13, 0, 0, TimeSpan.Zero);
        var expiredId = await CreateMediaAsync(
            MediaStatus.Processing,
            now,
            attemptCount: 1,
            leaseExpiresAt: now.AddSeconds(-1));
        var activeId = await CreateMediaAsync(
            MediaStatus.Processing,
            now,
            attemptCount: 1,
            leaseExpiresAt: now.AddMinutes(1));

        await using var db = _fixture.NewDbContext();
        var service = new MediaProcessingLeaseService(
            db,
            new MutableTimeProvider(now));

        var reclaimed = await service.TryAcquireAsync(
            expiredId,
            revision: 1,
            TestContext.Current.CancellationToken);
        var active = await service.TryAcquireAsync(
            activeId,
            revision: 1,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            MediaProcessingLeaseAcquisitionOutcome.Acquired,
            reclaimed.Outcome);
        Assert.Equal(2, reclaimed.Lease!.AttemptNumber);
        Assert.Equal(
            MediaProcessingLeaseAcquisitionOutcome.ActiveLease,
            active.Outcome);
    }

    [Fact]
    public async Task ExpiredSixthLease_IsFailedInsteadOfStartingASeventhAttempt()
    {
        var now = new DateTimeOffset(2026, 8, 16, 13, 30, 0, TimeSpan.Zero);
        var mediaId = await CreateMediaAsync(
            MediaStatus.Processing,
            now,
            attemptCount: MediaLifecyclePolicy.MaximumProcessingAttempts,
            leaseExpiresAt: now.AddSeconds(-1));

        await using var db = _fixture.NewDbContext();
        var result = await new MediaProcessingLeaseService(
                db,
                new MutableTimeProvider(now))
            .TryAcquireAsync(
                mediaId,
                revision: 1,
                TestContext.Current.CancellationToken);

        Assert.Equal(
            MediaProcessingLeaseAcquisitionOutcome.AttemptsExhausted,
            result.Outcome);
        Assert.Null(result.Lease);
        await using var verificationDb = _fixture.NewDbContext();
        var stored = await verificationDb.MediaAssets
            .AsNoTracking()
            .SingleAsync(
                media => media.Id == mediaId,
                TestContext.Current.CancellationToken);
        Assert.Equal(MediaStatus.Failed, stored.Status);
        Assert.Equal(
            MediaProcessingLeaseService.AttemptsExhaustedFailureCode,
            stored.FailureCode);
        Assert.Null(stored.ProcessingLeaseId);
    }

    [Fact]
    public async Task PendingRetry_IsNotAcquiredBeforeItsDueTime()
    {
        var now = new DateTimeOffset(2026, 8, 16, 14, 0, 0, TimeSpan.Zero);
        var mediaId = await CreateMediaAsync(
            MediaStatus.ProcessingPending,
            now,
            attemptCount: 1,
            nextAttemptAt: now.AddMinutes(1));

        await using var db = _fixture.NewDbContext();
        var service = new MediaProcessingLeaseService(
            db,
            new MutableTimeProvider(now));

        var result = await service.TryAcquireAsync(
            mediaId,
            revision: 1,
            TestContext.Current.CancellationToken);

        Assert.Equal(MediaProcessingLeaseAcquisitionOutcome.NotDue, result.Outcome);
        Assert.Null(result.Lease);
    }

    [Fact]
    public async Task TransientFailures_FollowBackoffThenFailAfterSixthAttempt()
    {
        var now = new DateTimeOffset(2026, 8, 16, 15, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var mediaId = await CreateMediaAsync(MediaStatus.ProcessingPending, now);
        var expectedDelays = new[]
        {
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(2),
            TimeSpan.FromHours(6),
        };

        await using var db = _fixture.NewDbContext();
        var service = new MediaProcessingLeaseService(db, clock);
        for (var attempt = 1; attempt <= MediaLifecyclePolicy.MaximumProcessingAttempts; attempt++)
        {
            var acquisition = await service.TryAcquireAsync(
                mediaId,
                revision: 1,
                TestContext.Current.CancellationToken);
            Assert.Equal(
                MediaProcessingLeaseAcquisitionOutcome.Acquired,
                acquisition.Outcome);
            Assert.Equal(attempt, acquisition.Lease!.AttemptNumber);

            var failure = await service.RecordTransientFailureAsync(
                acquisition.Lease,
                "object_storage_unavailable",
                TestContext.Current.CancellationToken);

            await using var verificationDb = _fixture.NewDbContext();
            var stored = await verificationDb.MediaAssets
                .AsNoTracking()
                .SingleAsync(
                    media => media.Id == mediaId,
                    TestContext.Current.CancellationToken);
            if (attempt < MediaLifecyclePolicy.MaximumProcessingAttempts)
            {
                Assert.Equal(MediaProcessingFailureOutcome.Requeued, failure);
                Assert.Equal(MediaStatus.ProcessingPending, stored.Status);
                Assert.Equal(clock.GetUtcNow() + expectedDelays[attempt - 1], stored.NextProcessingAttemptAt);
                Assert.Null(stored.ProcessingLeaseId);
                clock.UtcNow = stored.NextProcessingAttemptAt!.Value;
            }
            else
            {
                Assert.Equal(MediaProcessingFailureOutcome.Failed, failure);
                Assert.Equal(MediaStatus.Failed, stored.Status);
                Assert.Equal("processing_attempts_exhausted", stored.FailureCode);
                Assert.Null(stored.NextProcessingAttemptAt);
            }
        }
    }

    [Fact]
    public async Task PermanentFailure_ImmediatelyFinalizesOnlyTheOwnedLease()
    {
        var now = new DateTimeOffset(2026, 8, 16, 16, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var mediaId = await CreateMediaAsync(MediaStatus.ProcessingPending, now);

        await using var db = _fixture.NewDbContext();
        var service = new MediaProcessingLeaseService(db, clock);
        var acquisition = await service.TryAcquireAsync(
            mediaId,
            revision: 1,
            TestContext.Current.CancellationToken);
        var lease = acquisition.Lease!;

        var lost = await service.RecordPermanentFailureAsync(
            lease with { LeaseId = Guid.NewGuid() },
            "image_invalid_format",
            TestContext.Current.CancellationToken);
        var failed = await service.RecordPermanentFailureAsync(
            lease,
            "image_invalid_format",
            TestContext.Current.CancellationToken);

        Assert.Equal(MediaProcessingFailureOutcome.LeaseLost, lost);
        Assert.Equal(MediaProcessingFailureOutcome.Failed, failed);
        await using var verificationDb = _fixture.NewDbContext();
        var stored = await verificationDb.MediaAssets
            .AsNoTracking()
            .SingleAsync(
                media => media.Id == mediaId,
                TestContext.Current.CancellationToken);
        Assert.Equal(MediaStatus.Failed, stored.Status);
        Assert.Equal("image_invalid_format", stored.FailureCode);
        Assert.Null(stored.ProcessingLeaseId);
    }

    [Fact]
    public async Task UnexpectedAttemptError_IsConvertedToDurableRetry()
    {
        var now = new DateTimeOffset(2026, 8, 16, 17, 0, 0, TimeSpan.Zero);
        var mediaId = await CreateMediaAsync(MediaStatus.ProcessingPending, now);

        await using var db = _fixture.NewDbContext();
        var executor = new MediaProcessingWorkExecutor(
            new MediaProcessingLeaseService(db, new MutableTimeProvider(now)),
            new ThrowingAttemptExecutor(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<
                MediaProcessingWorkExecutor>.Instance);

        await executor.ExecuteAsync(
            mediaId,
            revision: 1,
            TestContext.Current.CancellationToken);

        await using var verificationDb = _fixture.NewDbContext();
        var stored = await verificationDb.MediaAssets
            .AsNoTracking()
            .SingleAsync(
                media => media.Id == mediaId,
                TestContext.Current.CancellationToken);
        Assert.Equal(MediaStatus.ProcessingPending, stored.Status);
        Assert.Equal(1, stored.ProcessingAttemptCount);
        Assert.Equal(now.AddMinutes(1), stored.NextProcessingAttemptAt);
        Assert.Null(stored.ProcessingLeaseId);
    }

    [Fact]
    public async Task RecoveryBatch_SelectsDueAndExpiredWorkOnly()
    {
        var now = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var dueId = await CreateMediaAsync(
            MediaStatus.ProcessingPending,
            now,
            attemptCount: 1,
            nextAttemptAt: now);
        var expiredId = await CreateMediaAsync(
            MediaStatus.Processing,
            now,
            attemptCount: 1,
            leaseExpiresAt: now.AddSeconds(-1));
        _ = await CreateMediaAsync(
            MediaStatus.ProcessingPending,
            now,
            attemptCount: 1,
            nextAttemptAt: now.AddMinutes(1));
        _ = await CreateMediaAsync(
            MediaStatus.Processing,
            now,
            attemptCount: 1,
            leaseExpiresAt: now.AddMinutes(1));
        var executor = new RecordingWorkExecutor();

        await using var db = _fixture.NewDbContext();
        var recovery = new MediaProcessingRecoveryService(
            db,
            executor,
            new MutableTimeProvider(now));

        var attempted = await recovery.RunBatchAsync(
            batchSize: 2,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, attempted);
        Assert.Equal(
            new[] { dueId, expiredId }.Order().ToArray(),
            executor.Started.Select(item => item.MediaId).Order().ToArray());
    }

    private async Task<Guid> CreateMediaAsync(
        MediaStatus status,
        DateTimeOffset now,
        int attemptCount = 0,
        DateTimeOffset? nextAttemptAt = null,
        DateTimeOffset? leaseExpiresAt = null)
    {
        await using var db = _fixture.NewDbContext();
        var owner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var game = await ReviewTestData.CreateGameAsync(db, owner.Id);
        var media = new MediaAsset
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner.Id,
            GameId = game.Id,
            AttachmentKind = MediaAttachmentKind.GameScreenshot,
            Status = status,
            DeclaredContentType = "image/png",
            DeclaredBytes = 2048,
            SourceContentType = "image/png",
            SourceBytes = 2048,
            SourceETag = $"\"{Guid.NewGuid():N}\"",
            ProcessingAttemptCount = attemptCount,
            ProcessingLeaseId = status == MediaStatus.Processing
                ? Guid.NewGuid()
                : null,
            ProcessingLeaseExpiresAt = status == MediaStatus.Processing
                ? leaseExpiresAt
                : null,
            NextProcessingAttemptAt = nextAttemptAt,
            CreatedAt = now.AddHours(-1),
            UpdatedAt = now.AddMinutes(-20),
            StatusChangedAt = now.AddMinutes(-20),
            UploadExpiresAt = now.AddHours(23),
        };
        db.MediaAssets.Add(media);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return media.Id;
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        public MutableTimeProvider(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; set; }

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class ThrowingAttemptExecutor : IMediaProcessingAttemptExecutor
    {
        public Task<MediaProcessingAttemptResult> ExecuteAsync(
            MediaProcessingLease lease,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated decoder crash.");
    }

    private sealed class RecordingWorkExecutor : IMediaProcessingWorkExecutor
    {
        public List<(Guid MediaId, int Revision)> Started { get; } = [];

        public Task ExecuteAsync(
            Guid mediaId,
            int revision,
            CancellationToken cancellationToken = default)
        {
            Started.Add((mediaId, revision));
            return Task.CompletedTask;
        }
    }
}
