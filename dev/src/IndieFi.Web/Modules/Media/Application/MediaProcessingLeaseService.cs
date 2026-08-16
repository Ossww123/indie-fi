using IndieFi.Web.Data;
using IndieFi.Web.Modules.Media.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Media.Application;

public enum MediaProcessingLeaseAcquisitionOutcome
{
    Acquired,
    MediaNotFound,
    RevisionMismatch,
    NotEligible,
    NotDue,
    ActiveLease,
    AttemptsExhausted,
}

public enum MediaProcessingFailureOutcome
{
    Requeued,
    Failed,
    LeaseLost,
}

public sealed record MediaProcessingLease(
    Guid MediaId,
    int Revision,
    Guid LeaseId,
    int AttemptNumber,
    DateTimeOffset ExpiresAt);

public sealed record MediaProcessingLeaseAcquisitionResult(
    MediaProcessingLeaseAcquisitionOutcome Outcome,
    MediaProcessingLease? Lease = null);

public sealed class MediaProcessingLeaseService
{
    public const string AttemptsExhaustedFailureCode =
        "processing_attempts_exhausted";

    private readonly AppDbContext _db;
    private readonly TimeProvider _timeProvider;

    public MediaProcessingLeaseService(
        AppDbContext db,
        TimeProvider timeProvider)
    {
        _db = db;
        _timeProvider = timeProvider;
    }

    public async Task<MediaProcessingLeaseAcquisitionResult> TryAcquireAsync(
        Guid mediaId,
        int revision,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(mediaId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);

        var now = PostgreSqlTimestamp(_timeProvider.GetUtcNow());
        var exhausted = await _db.MediaAssets
            .Where(media =>
                media.Id == mediaId
                && media.Revision == revision
                && media.ProcessingAttemptCount
                    >= MediaLifecyclePolicy.MaximumProcessingAttempts
                && ((media.Status == MediaStatus.ProcessingPending
                        && (media.NextProcessingAttemptAt == null
                            || media.NextProcessingAttemptAt <= now))
                    || (media.Status == MediaStatus.Processing
                        && media.ProcessingLeaseExpiresAt <= now)))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(media => media.Status, MediaStatus.Failed)
                    .SetProperty(
                        media => media.FailureCode,
                        AttemptsExhaustedFailureCode)
                    .SetProperty(media => media.ProcessingLeaseId, (Guid?)null)
                    .SetProperty(
                        media => media.ProcessingLeaseExpiresAt,
                        (DateTimeOffset?)null)
                    .SetProperty(
                        media => media.NextProcessingAttemptAt,
                        (DateTimeOffset?)null)
                    .SetProperty(media => media.StatusChangedAt, now)
                    .SetProperty(media => media.UpdatedAt, now),
                cancellationToken);
        if (exhausted == 1)
        {
            return new MediaProcessingLeaseAcquisitionResult(
                MediaProcessingLeaseAcquisitionOutcome.AttemptsExhausted);
        }

        var leaseId = Guid.NewGuid();
        var leaseExpiresAt = now + MediaLifecyclePolicy.ProcessingLeaseLifetime;
        var acquired = await _db.MediaAssets
            .Where(media =>
                media.Id == mediaId
                && media.Revision == revision
                && media.ProcessingAttemptCount
                    < MediaLifecyclePolicy.MaximumProcessingAttempts
                && ((media.Status == MediaStatus.ProcessingPending
                        && (media.NextProcessingAttemptAt == null
                            || media.NextProcessingAttemptAt <= now))
                    || (media.Status == MediaStatus.Processing
                        && media.ProcessingLeaseExpiresAt <= now)))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(media => media.Status, MediaStatus.Processing)
                    .SetProperty(
                        media => media.ProcessingAttemptCount,
                        media => media.ProcessingAttemptCount + 1)
                    .SetProperty(media => media.ProcessingLeaseId, leaseId)
                    .SetProperty(
                        media => media.ProcessingLeaseExpiresAt,
                        leaseExpiresAt)
                    .SetProperty(
                        media => media.NextProcessingAttemptAt,
                        (DateTimeOffset?)null)
                    .SetProperty(
                        media => media.StatusChangedAt,
                        media => media.Status == MediaStatus.ProcessingPending
                            ? now
                            : media.StatusChangedAt)
                    .SetProperty(media => media.UpdatedAt, now),
                cancellationToken);
        if (acquired == 1)
        {
            var attemptNumber = await _db.MediaAssets
                .AsNoTracking()
                .Where(media =>
                    media.Id == mediaId
                    && media.Revision == revision
                    && media.ProcessingLeaseId == leaseId)
                .Select(media => media.ProcessingAttemptCount)
                .SingleAsync(cancellationToken);
            return new MediaProcessingLeaseAcquisitionResult(
                MediaProcessingLeaseAcquisitionOutcome.Acquired,
                new MediaProcessingLease(
                    mediaId,
                    revision,
                    leaseId,
                    attemptNumber,
                    leaseExpiresAt));
        }

        var state = await _db.MediaAssets
            .AsNoTracking()
            .Where(media => media.Id == mediaId)
            .Select(media => new MediaProcessingState(
                media.Revision,
                media.Status,
                media.NextProcessingAttemptAt,
                media.ProcessingLeaseExpiresAt))
            .SingleOrDefaultAsync(cancellationToken);
        if (state is null)
        {
            return Result(MediaProcessingLeaseAcquisitionOutcome.MediaNotFound);
        }

        if (state.Revision != revision)
        {
            return Result(MediaProcessingLeaseAcquisitionOutcome.RevisionMismatch);
        }

        if (state.Status == MediaStatus.ProcessingPending
            && state.NextAttemptAt > now)
        {
            return Result(MediaProcessingLeaseAcquisitionOutcome.NotDue);
        }

        if (state.Status == MediaStatus.Processing
            && state.LeaseExpiresAt > now)
        {
            return Result(MediaProcessingLeaseAcquisitionOutcome.ActiveLease);
        }

        return Result(MediaProcessingLeaseAcquisitionOutcome.NotEligible);
    }

    public async Task<MediaProcessingFailureOutcome> RecordTransientFailureAsync(
        MediaProcessingLease lease,
        string failureCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        MediaProcessingFailureCode.RequireValid(failureCode);

        var now = PostgreSqlTimestamp(_timeProvider.GetUtcNow());
        if (!MediaLifecyclePolicy.TryGetProcessingRetryDelay(
                lease.AttemptNumber,
                out var retryDelay))
        {
            var failed = await FinalizeFailureAsync(
                lease,
                AttemptsExhaustedFailureCode,
                now,
                cancellationToken);
            return failed == 1
                ? MediaProcessingFailureOutcome.Failed
                : MediaProcessingFailureOutcome.LeaseLost;
        }

        var nextAttemptAt = now + retryDelay;
        var requeued = await OwnedLease(lease, now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(
                        media => media.Status,
                        MediaStatus.ProcessingPending)
                    .SetProperty(media => media.ProcessingLeaseId, (Guid?)null)
                    .SetProperty(
                        media => media.ProcessingLeaseExpiresAt,
                        (DateTimeOffset?)null)
                    .SetProperty(
                        media => media.NextProcessingAttemptAt,
                        nextAttemptAt)
                    .SetProperty(media => media.StatusChangedAt, now)
                    .SetProperty(media => media.UpdatedAt, now),
                cancellationToken);
        return requeued == 1
            ? MediaProcessingFailureOutcome.Requeued
            : MediaProcessingFailureOutcome.LeaseLost;
    }

    public async Task<MediaProcessingFailureOutcome> RecordPermanentFailureAsync(
        MediaProcessingLease lease,
        string failureCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        MediaProcessingFailureCode.RequireValid(failureCode);

        var failed = await FinalizeFailureAsync(
            lease,
            failureCode,
            PostgreSqlTimestamp(_timeProvider.GetUtcNow()),
            cancellationToken);
        return failed == 1
            ? MediaProcessingFailureOutcome.Failed
            : MediaProcessingFailureOutcome.LeaseLost;
    }

    public Task<bool> IsLeaseActiveAsync(
        MediaProcessingLease lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var now = PostgreSqlTimestamp(_timeProvider.GetUtcNow());
        return OwnedLease(lease, now).AnyAsync(cancellationToken);
    }

    private Task<int> FinalizeFailureAsync(
        MediaProcessingLease lease,
        string failureCode,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        OwnedLease(lease, now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(media => media.Status, MediaStatus.Failed)
                    .SetProperty(media => media.FailureCode, failureCode)
                    .SetProperty(media => media.ProcessingLeaseId, (Guid?)null)
                    .SetProperty(
                        media => media.ProcessingLeaseExpiresAt,
                        (DateTimeOffset?)null)
                    .SetProperty(
                        media => media.NextProcessingAttemptAt,
                        (DateTimeOffset?)null)
                    .SetProperty(media => media.StatusChangedAt, now)
                    .SetProperty(media => media.UpdatedAt, now),
                cancellationToken);

    private IQueryable<MediaAsset> OwnedLease(
        MediaProcessingLease lease,
        DateTimeOffset now) =>
        _db.MediaAssets.Where(media =>
            media.Id == lease.MediaId
            && media.Revision == lease.Revision
            && media.Status == MediaStatus.Processing
            && media.ProcessingAttemptCount == lease.AttemptNumber
            && media.ProcessingLeaseId == lease.LeaseId
            && media.ProcessingLeaseExpiresAt > now);

    private static MediaProcessingLeaseAcquisitionResult Result(
        MediaProcessingLeaseAcquisitionOutcome outcome) => new(outcome);

    private static DateTimeOffset PostgreSqlTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime()
            .AddTicks(-(value.Ticks % TimeSpan.TicksPerMicrosecond));

    private sealed record MediaProcessingState(
        int Revision,
        MediaStatus Status,
        DateTimeOffset? NextAttemptAt,
        DateTimeOffset? LeaseExpiresAt);
}
