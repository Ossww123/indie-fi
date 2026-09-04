using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Media.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Media.Application;

public enum MediaUploadUrlOutcome
{
    Issued,
    AccountUnavailable,
    MediaNotFound,
    MediaNotPending,
    ReservationExpired,
    ShortWindowLimitReached,
    DailyLimitReached,
    RateLimiterUnavailable,
}

public sealed record MediaUploadUrlResult(
    MediaUploadUrlOutcome Outcome,
    Guid? UploadAttemptId = null,
    Uri? UploadUrl = null,
    DateTimeOffset? UrlExpiresAt = null,
    string? RequiredContentType = null,
    long? RequiredContentLength = null);

public sealed class MediaUploadUrlService
{
    private readonly AppDbContext _db;
    private readonly IMediaUploadIssueRateLimiter _rateLimiter;
    private readonly IMediaObjectStorage _storage;
    private readonly TimeProvider _timeProvider;

    public MediaUploadUrlService(
        AppDbContext db,
        IMediaUploadIssueRateLimiter rateLimiter,
        IMediaObjectStorage storage,
        TimeProvider timeProvider)
    {
        _db = db;
        _rateLimiter = rateLimiter;
        _storage = storage;
        _timeProvider = timeProvider;
    }

    public async Task<MediaUploadUrlResult> IssueAsync(
        long ownerUserId,
        Guid mediaId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ownerUserId);
        if (mediaId == Guid.Empty)
        {
            throw new ArgumentException(
                "A media identifier must not be empty.",
                nameof(mediaId));
        }

        await using var transaction =
            await AccountMutationGuard.BeginTransactionIfNeededAsync(
                _db,
                cancellationToken);

        if (!await AccountMutationGuard.LockActiveAsync(
                _db,
                ownerUserId,
                cancellationToken))
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new MediaUploadUrlResult(
                MediaUploadUrlOutcome.AccountUnavailable);
        }

        // Owner filtering keeps a foreign media identifier indistinguishable from
        // a missing one. The row lock serializes reissues for this reservation.
        var mediaRows = await _db.MediaAssets
            .FromSqlInterpolated(
                $"SELECT * FROM media_assets WHERE id = {mediaId} AND owner_user_id = {ownerUserId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        var media = mediaRows.SingleOrDefault();
        if (media is null)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new MediaUploadUrlResult(MediaUploadUrlOutcome.MediaNotFound);
        }

        if (media.Status != MediaStatus.UploadPending)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new MediaUploadUrlResult(MediaUploadUrlOutcome.MediaNotPending);
        }

        // PostgreSQL timestamptz stores microseconds. Normalize before returning
        // the same expiry that is persisted so the API never overstates precision.
        var currentTime = _timeProvider.GetUtcNow();
        var now = currentTime.AddTicks(
            -(currentTime.Ticks % TimeSpan.TicksPerMicrosecond));
        if (media.UploadExpiresAt <= now)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new MediaUploadUrlResult(MediaUploadUrlOutcome.ReservationExpired);
        }

        var uploadAttemptId = Guid.NewGuid();
        MediaUploadIssueRateLimitOutcome rateLimitOutcome;
        try
        {
            rateLimitOutcome = await _rateLimiter.TryAcquireAsync(
                ownerUserId,
                uploadAttemptId,
                cancellationToken);
        }
        catch (MediaUploadIssueRateLimitUnavailableException)
        {
            await AccountMutationGuard.RollbackIfOwnedAsync(
                transaction,
                cancellationToken);
            return new MediaUploadUrlResult(
                MediaUploadUrlOutcome.RateLimiterUnavailable);
        }

        var rejectedOutcome = rateLimitOutcome switch
        {
            MediaUploadIssueRateLimitOutcome.Allowed => (MediaUploadUrlOutcome?)null,
            MediaUploadIssueRateLimitOutcome.ShortWindowLimitReached =>
                MediaUploadUrlOutcome.ShortWindowLimitReached,
            MediaUploadIssueRateLimitOutcome.DailyLimitReached =>
                MediaUploadUrlOutcome.DailyLimitReached,
            _ => throw new InvalidOperationException(
                "The media upload rate limiter returned an unknown outcome."),
        };
        if (rejectedOutcome is not null)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new MediaUploadUrlResult(rejectedOutcome.Value);
        }

        var currentAttempt = await _db.MediaUploadAttempts.SingleOrDefaultAsync(
            item => item.MediaId == mediaId && item.SupersededAt == null,
            cancellationToken);
        if (currentAttempt is not null)
        {
            currentAttempt.SupersededAt = now;
            await _db.SaveChangesAsync(cancellationToken);
        }

        var attempt = new MediaUploadAttempt
        {
            Id = uploadAttemptId,
            MediaId = mediaId,
            IssuedAt = now,
            UrlExpiresAt = now.Add(MediaObjectAccessPolicy.UploadUrlLifetime),
        };
        _db.MediaUploadAttempts.Add(attempt);
        await _db.SaveChangesAsync(cancellationToken);

        var stagingKey = MediaObjectKey.Staging(mediaId, uploadAttemptId);
        var request = MediaObjectAccessPolicy.CreateUploadUrlRequest(
            stagingKey,
            UploadKind(media.AttachmentKind),
            media.DeclaredContentType,
            media.DeclaredBytes);
        var uploadUrl = await _storage.CreateUploadUrlAsync(
            request,
            cancellationToken);

        await AccountMutationGuard.CommitIfOwnedAsync(transaction, cancellationToken);
        return new MediaUploadUrlResult(
            MediaUploadUrlOutcome.Issued,
            uploadAttemptId,
            uploadUrl,
            attempt.UrlExpiresAt,
            media.DeclaredContentType,
            media.DeclaredBytes);
    }

    private static MediaUploadKind UploadKind(
        MediaAttachmentKind attachmentKind) => attachmentKind switch
        {
            MediaAttachmentKind.GameCover => MediaUploadKind.GameImage,
            MediaAttachmentKind.GameScreenshot => MediaUploadKind.GameImage,
            MediaAttachmentKind.ProfileImage => MediaUploadKind.ProfileImage,
            _ => throw new ArgumentOutOfRangeException(nameof(attachmentKind)),
        };
}
