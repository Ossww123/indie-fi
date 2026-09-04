using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Domain;
using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class MediaUploadUrlServiceTests
{
    private readonly PostgreSqlFixture _fixture;

    public MediaUploadUrlServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task IssueAsync_ForOwnedPendingMedia_PersistsExactAttemptAndRequest()
    {
        var reserved = await CreatePendingMediaAsync(
            MediaAttachmentKind.GameScreenshot,
            "image/png",
            2048);
        var storage = new RecordingMediaObjectStorage();
        var now = PostgreSqlNow();

        MediaUploadUrlResult result;
        await using (var db = _fixture.NewDbContext())
        {
            result = await NewService(db, storage, now: now).IssueAsync(
                reserved.OwnerId,
                reserved.MediaId,
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(MediaUploadUrlOutcome.Issued, result.Outcome);
        Assert.NotNull(result.UploadAttemptId);
        Assert.Equal("image/png", result.RequiredContentType);
        Assert.Equal(2048, result.RequiredContentLength);
        Assert.Equal(now.AddMinutes(15), result.UrlExpiresAt);
        var request = Assert.Single(storage.UploadRequests);
        Assert.Equal(
            MediaObjectKey.Staging(reserved.MediaId, result.UploadAttemptId!.Value),
            request.ObjectKey);
        Assert.Equal(MediaUploadKind.GameImage, request.UploadKind);
        Assert.Equal("image/png", request.ContentType);
        Assert.Equal(2048, request.ContentLength);
        Assert.Equal(
            $"https://upload.example.test/{request.ObjectKey.Value}",
            result.UploadUrl!.AbsoluteUri);

        await using var verificationDb = _fixture.NewDbContext();
        var stored = await verificationDb.MediaUploadAttempts
            .AsNoTracking()
            .SingleAsync(
                item => item.MediaId == reserved.MediaId,
                TestContext.Current.CancellationToken);
        Assert.Equal(result.UploadAttemptId, stored.Id);
        Assert.Equal(now, stored.IssuedAt);
        Assert.Equal(now.AddMinutes(15), stored.UrlExpiresAt);
        Assert.Null(stored.SupersededAt);
        Assert.Null(stored.CompletedAt);
    }

    [Fact]
    public async Task IssueAsync_Reissue_SupersedesPriorAttemptAndUsesNewKey()
    {
        var reserved = await CreatePendingMediaAsync(
            MediaAttachmentKind.ProfileImage,
            "image/webp",
            1024,
            createProfile: true);
        var storage = new RecordingMediaObjectStorage();
        var firstAt = PostgreSqlNow();

        MediaUploadUrlResult first;
        await using (var firstDb = _fixture.NewDbContext())
        {
            first = await NewService(firstDb, storage, now: firstAt).IssueAsync(
                reserved.OwnerId,
                reserved.MediaId,
                TestContext.Current.CancellationToken);
        }

        var secondAt = firstAt.AddMinutes(1);
        MediaUploadUrlResult second;
        await using (var secondDb = _fixture.NewDbContext())
        {
            second = await NewService(secondDb, storage, now: secondAt).IssueAsync(
                reserved.OwnerId,
                reserved.MediaId,
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(MediaUploadUrlOutcome.Issued, first.Outcome);
        Assert.Equal(MediaUploadUrlOutcome.Issued, second.Outcome);
        Assert.NotEqual(first.UploadAttemptId, second.UploadAttemptId);
        Assert.Equal(2, storage.UploadRequests.Count);
        Assert.All(
            storage.UploadRequests,
            request => Assert.Equal(MediaUploadKind.ProfileImage, request.UploadKind));
        Assert.NotEqual(
            storage.UploadRequests[0].ObjectKey,
            storage.UploadRequests[1].ObjectKey);

        await using var verificationDb = _fixture.NewDbContext();
        var attempts = await verificationDb.MediaUploadAttempts
            .AsNoTracking()
            .Where(item => item.MediaId == reserved.MediaId)
            .OrderBy(item => item.IssuedAt)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, attempts.Count);
        Assert.Equal(secondAt, attempts[0].SupersededAt);
        Assert.Null(attempts[1].SupersededAt);
    }

    [Theory]
    [InlineData(MediaUploadIssueRateLimitOutcome.ShortWindowLimitReached,
        MediaUploadUrlOutcome.ShortWindowLimitReached)]
    [InlineData(MediaUploadIssueRateLimitOutcome.DailyLimitReached,
        MediaUploadUrlOutcome.DailyLimitReached)]
    public async Task IssueAsync_WhenRateLimited_DoesNotSupersedeCurrentAttempt(
        MediaUploadIssueRateLimitOutcome limiterOutcome,
        MediaUploadUrlOutcome expectedOutcome)
    {
        var reserved = await CreatePendingMediaAsync();
        var storage = new RecordingMediaObjectStorage();
        var now = PostgreSqlNow();

        MediaUploadUrlResult first;
        await using (var firstDb = _fixture.NewDbContext())
        {
            first = await NewService(firstDb, storage, now: now).IssueAsync(
                reserved.OwnerId,
                reserved.MediaId,
                TestContext.Current.CancellationToken);
        }

        await using (var limitedDb = _fixture.NewDbContext())
        {
            var limited = await NewService(
                limitedDb,
                storage,
                new FixedRateLimiter(limiterOutcome),
                now.AddMinutes(1)).IssueAsync(
                    reserved.OwnerId,
                    reserved.MediaId,
                    TestContext.Current.CancellationToken);
            Assert.Equal(expectedOutcome, limited.Outcome);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var attempt = await verificationDb.MediaUploadAttempts
            .AsNoTracking()
            .SingleAsync(
                item => item.MediaId == reserved.MediaId,
                TestContext.Current.CancellationToken);
        Assert.Equal(first.UploadAttemptId, attempt.Id);
        Assert.Null(attempt.SupersededAt);
        Assert.Single(storage.UploadRequests);
    }

    [Fact]
    public async Task IssueAsync_WhenRateLimiterIsUnavailable_FailsClosedWithoutAttempt()
    {
        var reserved = await CreatePendingMediaAsync();
        var storage = new RecordingMediaObjectStorage();

        await using var db = _fixture.NewDbContext();
        var result = await NewService(
            db,
            storage,
            ThrowingRateLimiter.Instance).IssueAsync(
                reserved.OwnerId,
                reserved.MediaId,
                TestContext.Current.CancellationToken);

        Assert.Equal(MediaUploadUrlOutcome.RateLimiterUnavailable, result.Outcome);
        Assert.False(await db.MediaUploadAttempts.AnyAsync(
            item => item.MediaId == reserved.MediaId,
            TestContext.Current.CancellationToken));
        Assert.Empty(storage.UploadRequests);
    }

    [Fact]
    public async Task IssueAsync_ForForeignMedia_ReturnsNotFoundWithoutConsumingLimit()
    {
        var reserved = await CreatePendingMediaAsync();
        long otherOwnerId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            otherOwnerId = (await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0)).Id;
        }

        var limiter = new FixedRateLimiter();
        await using var db = _fixture.NewDbContext();
        var result = await NewService(
            db,
            new RecordingMediaObjectStorage(),
            limiter).IssueAsync(
                otherOwnerId,
                reserved.MediaId,
                TestContext.Current.CancellationToken);

        Assert.Equal(MediaUploadUrlOutcome.MediaNotFound, result.Outcome);
        Assert.Equal(0, limiter.CallCount);
    }

    [Fact]
    public async Task IssueAsync_ForSuspendedOwner_ReturnsAccountUnavailable()
    {
        var reserved = await CreatePendingMediaAsync();
        await using (var ownerDb = _fixture.NewOwnerDbContext())
        {
            var user = await ownerDb.Users.SingleAsync(
                item => item.Id == reserved.OwnerId,
                TestContext.Current.CancellationToken);
            user.AccountStatus = AccountStatus.IndefinitelySuspended;
            await ownerDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var limiter = new FixedRateLimiter();
        await using var db = _fixture.NewDbContext();
        var result = await NewService(
            db,
            new RecordingMediaObjectStorage(),
            limiter).IssueAsync(
                reserved.OwnerId,
                reserved.MediaId,
                TestContext.Current.CancellationToken);

        Assert.Equal(MediaUploadUrlOutcome.AccountUnavailable, result.Outcome);
        Assert.Equal(0, limiter.CallCount);
    }

    [Fact]
    public async Task IssueAsync_ForExpiredReservation_DoesNotConsumeLimit()
    {
        var reserved = await CreatePendingMediaAsync();
        var limiter = new FixedRateLimiter();

        await using var db = _fixture.NewDbContext();
        var result = await NewService(
            db,
            new RecordingMediaObjectStorage(),
            limiter,
            reserved.UploadExpiresAt).IssueAsync(
                reserved.OwnerId,
                reserved.MediaId,
                TestContext.Current.CancellationToken);

        Assert.Equal(MediaUploadUrlOutcome.ReservationExpired, result.Outcome);
        Assert.Equal(0, limiter.CallCount);
    }

    [Fact]
    public async Task IssueAsync_WhenSigningFails_RollsBackNewAndSupersededAttempts()
    {
        var reserved = await CreatePendingMediaAsync();
        var storage = new RecordingMediaObjectStorage();
        var firstAt = PostgreSqlNow();

        MediaUploadUrlResult first;
        await using (var firstDb = _fixture.NewDbContext())
        {
            first = await NewService(firstDb, storage, now: firstAt).IssueAsync(
                reserved.OwnerId,
                reserved.MediaId,
                TestContext.Current.CancellationToken);
        }

        storage.ThrowOnUpload = true;
        await using (var failingDb = _fixture.NewDbContext())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                NewService(failingDb, storage, now: firstAt.AddMinutes(1)).IssueAsync(
                    reserved.OwnerId,
                    reserved.MediaId,
                    TestContext.Current.CancellationToken));
        }

        await using var verificationDb = _fixture.NewDbContext();
        var attempt = await verificationDb.MediaUploadAttempts
            .AsNoTracking()
            .SingleAsync(
                item => item.MediaId == reserved.MediaId,
                TestContext.Current.CancellationToken);
        Assert.Equal(first.UploadAttemptId, attempt.Id);
        Assert.Null(attempt.SupersededAt);
    }

    private async Task<PendingMedia> CreatePendingMediaAsync(
        MediaAttachmentKind attachmentKind = MediaAttachmentKind.GameScreenshot,
        string contentType = "image/jpeg",
        long declaredBytes = 1024,
        bool createProfile = false)
    {
        await using var db = _fixture.NewDbContext();
        var owner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var game = new Game
        {
            OwnerId = owner.Id,
            Title = $"Upload URL game {Guid.NewGuid():N}",
        };
        db.Games.Add(game);
        if (createProfile)
        {
            db.UserProfiles.Add(new Modules.Profiles.Domain.UserProfile
            {
                UserId = owner.Id,
            });
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var media = new MediaAsset
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner.Id,
            GameId = createProfile ? null : game.Id,
            ProfileUserId = createProfile ? owner.Id : null,
            AttachmentKind = attachmentKind,
            DeclaredContentType = contentType,
            DeclaredBytes = declaredBytes,
        };
        db.MediaAssets.Add(media);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return new PendingMedia(owner.Id, media.Id, media.UploadExpiresAt);
    }

    private static MediaUploadUrlService NewService(
        AppDbContext db,
        RecordingMediaObjectStorage storage,
        IMediaUploadIssueRateLimiter? rateLimiter = null,
        DateTimeOffset? now = null) =>
        new(
            db,
            rateLimiter ?? new FixedRateLimiter(),
            storage,
            new FixedTimeProvider(now ?? DateTimeOffset.UtcNow));

    private static DateTimeOffset PostgreSqlNow()
    {
        var now = DateTimeOffset.UtcNow;
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
    }

    private sealed record PendingMedia(
        long OwnerId,
        Guid MediaId,
        DateTimeOffset UploadExpiresAt);

    private sealed class FixedRateLimiter : IMediaUploadIssueRateLimiter
    {
        private readonly MediaUploadIssueRateLimitOutcome _outcome;

        public FixedRateLimiter(
            MediaUploadIssueRateLimitOutcome outcome =
                MediaUploadIssueRateLimitOutcome.Allowed)
        {
            _outcome = outcome;
        }

        public int CallCount { get; private set; }

        public Task<MediaUploadIssueRateLimitOutcome> TryAcquireAsync(
            long ownerUserId,
            Guid issueId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_outcome);
        }
    }

    private sealed class ThrowingRateLimiter : IMediaUploadIssueRateLimiter
    {
        public static readonly ThrowingRateLimiter Instance = new();

        public Task<MediaUploadIssueRateLimitOutcome> TryAcquireAsync(
            long ownerUserId,
            Guid issueId,
            CancellationToken cancellationToken = default) =>
            throw new MediaUploadIssueRateLimitUnavailableException(
                "Unavailable for test.",
                new InvalidOperationException());
    }

    private sealed class RecordingMediaObjectStorage : IMediaObjectStorage
    {
        public List<ObjectStorageUploadUrlRequest> UploadRequests { get; } = [];

        public bool ThrowOnUpload { get; set; }

        public Task<Uri> CreateUploadUrlAsync(
            ObjectStorageUploadUrlRequest request,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnUpload)
            {
                throw new InvalidOperationException("Signing failed for test.");
            }

            UploadRequests.Add(request);
            return Task.FromResult(new Uri(
                $"https://upload.example.test/{request.ObjectKey.Value}"));
        }

        public Task<Uri> CreateDownloadUrlAsync(
            ObjectStorageDownloadUrlRequest request,
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
