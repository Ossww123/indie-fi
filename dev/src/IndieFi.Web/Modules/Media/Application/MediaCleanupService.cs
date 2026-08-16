using IndieFi.Web.Data;
using IndieFi.Web.Modules.Media.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Media.Application;

public sealed record MediaCleanupBatchResult(
    int ExaminedCount,
    int ExpiredCount,
    int ReadySourceDeletedCount,
    int DeletionCompletedCount,
    int TombstoneRemovedCount,
    int FailedCount);

public sealed class MediaCleanupService
{
    private readonly AppDbContext _db;
    private readonly MediaObjectDeletionService _objectDeletion;
    private readonly MediaDeletionWorkExecutor _deletionExecutor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MediaCleanupService> _logger;

    public MediaCleanupService(
        AppDbContext db,
        MediaObjectDeletionService objectDeletion,
        MediaDeletionWorkExecutor deletionExecutor,
        TimeProvider timeProvider,
        ILogger<MediaCleanupService> logger)
    {
        _db = db;
        _objectDeletion = objectDeletion;
        _deletionExecutor = deletionExecutor;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<MediaCleanupBatchResult> RunBatchAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        var now = PostgreSqlTimestamp(_timeProvider.GetUtcNow());
        var candidates = await _db.MediaAssets
            .AsNoTracking()
            .Where(media =>
                (media.Status == MediaStatus.UploadPending
                    && media.UploadExpiresAt <= now)
                || media.Status == MediaStatus.DeletePending
                || (media.Status == MediaStatus.Ready
                    && media.SourceDeletedAt == null)
                || ((media.Status == MediaStatus.Failed
                        || media.Status == MediaStatus.Expired
                        || media.Status == MediaStatus.Deleted)
                    && (media.DeletionCompletedAt == null
                        || media.TombstoneExpiresAt <= now)))
            .OrderBy(media => media.UpdatedAt)
            .ThenBy(media => media.Id)
            .Take(batchSize)
            .Select(media => new CleanupCandidate(
                media.Id,
                media.Revision,
                media.Status,
                media.DeletionCompletedAt,
                media.TombstoneExpiresAt))
            .ToArrayAsync(cancellationToken);

        var expired = 0;
        var readySourceDeleted = 0;
        var deletionCompleted = 0;
        var tombstoneRemoved = 0;
        var failed = 0;
        foreach (var candidate in candidates)
        {
            try
            {
                if (candidate.DeletionCompletedAt is not null
                    && candidate.TombstoneExpiresAt <= now)
                {
                    tombstoneRemoved += await RemoveTombstoneAsync(
                        candidate,
                        now,
                        cancellationToken);
                }
                else if (candidate.Status == MediaStatus.DeletePending)
                {
                    await _deletionExecutor.ExecuteAsync(
                        candidate.Id,
                        candidate.Revision,
                        cancellationToken);
                    deletionCompleted++;
                }
                else if (candidate.Status == MediaStatus.UploadPending)
                {
                    expired += await ExpireUploadAsync(
                        candidate,
                        now,
                        cancellationToken);
                }
                else if (candidate.Status == MediaStatus.Ready)
                {
                    readySourceDeleted += await DeleteReadySourceAsync(
                        candidate,
                        now,
                        cancellationToken);
                }
                else
                {
                    deletionCompleted += await CompleteTerminalCleanupAsync(
                        candidate,
                        now,
                        cancellationToken);
                }
            }
            catch (MediaObjectStorageException exception)
            {
                failed++;
                _logger.LogWarning(
                    exception,
                    "Media cleanup object deletion failed mediaId={MediaId} revision={Revision} status={Status} failureCode={FailureCode}",
                    candidate.Id,
                    candidate.Revision,
                    candidate.Status,
                    exception.FailureCode);
            }
        }

        return new MediaCleanupBatchResult(
            candidates.Length,
            expired,
            readySourceDeleted,
            deletionCompleted,
            tombstoneRemoved,
            failed);
    }

    private async Task<int> ExpireUploadAsync(
        CleanupCandidate candidate,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await DeleteAllObjectsAsync(candidate, cancellationToken);
        return await _db.MediaAssets
            .Where(media => media.Id == candidate.Id
                && media.Revision == candidate.Revision
                && media.Status == MediaStatus.UploadPending
                && media.UploadExpiresAt <= now)
            .ExecuteUpdateAsync(
                setters => TerminalSetters(
                    setters,
                    MediaStatus.Expired,
                    now,
                    clearFailureCode: true),
                cancellationToken);
    }

    private async Task<int> DeleteReadySourceAsync(
        CleanupCandidate candidate,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var attemptIds = await GetUploadAttemptIdsAsync(
            candidate.Id,
            cancellationToken);
        await _objectDeletion.DeleteStagingAndSourceAsync(
            candidate.Id,
            candidate.Revision,
            attemptIds,
            cancellationToken);
        return await _db.MediaAssets
            .Where(media => media.Id == candidate.Id
                && media.Revision == candidate.Revision
                && media.Status == MediaStatus.Ready
                && media.SourceDeletedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(media => media.SourceDeletedAt, now)
                    .SetProperty(media => media.UpdatedAt, now),
                cancellationToken);
    }

    private async Task<int> CompleteTerminalCleanupAsync(
        CleanupCandidate candidate,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await DeleteAllObjectsAsync(candidate, cancellationToken);
        return await _db.MediaAssets
            .Where(media => media.Id == candidate.Id
                && media.Revision == candidate.Revision
                && media.Status == candidate.Status
                && media.DeletionCompletedAt == null)
            .ExecuteUpdateAsync(
                setters => TerminalSetters(
                    setters,
                    candidate.Status,
                    now,
                    clearFailureCode: false),
                cancellationToken);
    }

    private async Task<int> RemoveTombstoneAsync(
        CleanupCandidate candidate,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await _db.MediaAssets
            .Where(media => media.Id == candidate.Id
                && media.Revision == candidate.Revision
                && (media.Status == MediaStatus.Failed
                    || media.Status == MediaStatus.Expired
                    || media.Status == MediaStatus.Deleted)
                && media.DeletionCompletedAt != null
                && media.TombstoneExpiresAt <= now)
            .ExecuteDeleteAsync(cancellationToken);

    private async Task DeleteAllObjectsAsync(
        CleanupCandidate candidate,
        CancellationToken cancellationToken)
    {
        var attemptIds = await GetUploadAttemptIdsAsync(
            candidate.Id,
            cancellationToken);
        await _objectDeletion.DeleteAllAsync(
            candidate.Id,
            candidate.Revision,
            attemptIds,
            cancellationToken);
    }

    private async Task<Guid[]> GetUploadAttemptIdsAsync(
        Guid mediaId,
        CancellationToken cancellationToken) =>
        await _db.MediaUploadAttempts
            .AsNoTracking()
            .Where(attempt => attempt.MediaId == mediaId)
            .Select(attempt => attempt.Id)
            .ToArrayAsync(cancellationToken);

    private static Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<MediaAsset>
        TerminalSetters(
            Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<MediaAsset> setters,
            MediaStatus status,
            DateTimeOffset now,
            bool clearFailureCode)
    {
        var updated = setters
            .SetProperty(media => media.Status, status)
            .SetProperty(media => media.SourceContentType, (string?)null)
            .SetProperty(media => media.SourceBytes, (long?)null)
            .SetProperty(media => media.SourceETag, (string?)null)
            .SetProperty(media => media.DetectedContentType, (string?)null)
            .SetProperty(media => media.PixelWidth, (int?)null)
            .SetProperty(media => media.PixelHeight, (int?)null)
            .SetProperty(media => media.FrameCount, (int?)null)
            .SetProperty(media => media.ProcessingLeaseId, (Guid?)null)
            .SetProperty(
                media => media.ProcessingLeaseExpiresAt,
                (DateTimeOffset?)null)
            .SetProperty(
                media => media.NextProcessingAttemptAt,
                (DateTimeOffset?)null)
            .SetProperty(media => media.SourceDeletedAt, now)
            .SetProperty(media => media.DeletionCompletedAt, now)
            .SetProperty(
                media => media.TombstoneExpiresAt,
                now + MediaLifecyclePolicy.TombstoneRetention)
            .SetProperty(media => media.IsCurrent, false)
            .SetProperty(media => media.SortOrder, (int?)null)
            .SetProperty(media => media.PublishedAt, (DateTimeOffset?)null)
            .SetProperty(media => media.StatusChangedAt, now)
            .SetProperty(media => media.UpdatedAt, now);
        return clearFailureCode
            ? updated.SetProperty(media => media.FailureCode, (string?)null)
            : updated;
    }

    private static DateTimeOffset PostgreSqlTimestamp(DateTimeOffset value) =>
        value.AddTicks(-(value.Ticks % TimeSpan.TicksPerMicrosecond));

    private sealed record CleanupCandidate(
        Guid Id,
        int Revision,
        MediaStatus Status,
        DateTimeOffset? DeletionCompletedAt,
        DateTimeOffset? TombstoneExpiresAt);
}
