using IndieFi.Web.Data;
using IndieFi.Web.Modules.Media.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Media.Application;

public sealed class MediaDeletionWorkExecutor : IMediaDeletionWorkExecutor
{
    private readonly AppDbContext _db;
    private readonly MediaObjectDeletionService _objectDeletion;
    private readonly TimeProvider _timeProvider;

    public MediaDeletionWorkExecutor(
        AppDbContext db,
        MediaObjectDeletionService objectDeletion,
        TimeProvider timeProvider)
    {
        _db = db;
        _objectDeletion = objectDeletion;
        _timeProvider = timeProvider;
    }

    public async Task ExecuteAsync(
        Guid mediaId,
        int revision,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(mediaId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);

        var isPending = await _db.MediaAssets.AsNoTracking().AnyAsync(
            media => media.Id == mediaId
                && media.Revision == revision
                && media.Status == MediaStatus.DeletePending,
            cancellationToken);
        if (!isPending)
        {
            return;
        }

        var uploadAttemptIds = await _db.MediaUploadAttempts
            .AsNoTracking()
            .Where(attempt => attempt.MediaId == mediaId)
            .Select(attempt => attempt.Id)
            .ToArrayAsync(cancellationToken);
        await _objectDeletion.DeleteAllAsync(
            mediaId,
            revision,
            uploadAttemptIds,
            cancellationToken);

        var now = PostgreSqlTimestamp(_timeProvider.GetUtcNow());
        await _db.MediaAssets
            .Where(media => media.Id == mediaId
                && media.Revision == revision
                && media.Status == MediaStatus.DeletePending)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(media => media.Status, MediaStatus.Deleted)
                    .SetProperty(media => media.SourceContentType, (string?)null)
                    .SetProperty(media => media.SourceBytes, (long?)null)
                    .SetProperty(media => media.SourceETag, (string?)null)
                    .SetProperty(media => media.DetectedContentType, (string?)null)
                    .SetProperty(media => media.PixelWidth, (int?)null)
                    .SetProperty(media => media.PixelHeight, (int?)null)
                    .SetProperty(media => media.FrameCount, (int?)null)
                    .SetProperty(media => media.FailureCode, (string?)null)
                    .SetProperty(media => media.SourceDeletedAt, now)
                    .SetProperty(media => media.DeletionCompletedAt, now)
                    .SetProperty(
                        media => media.TombstoneExpiresAt,
                        now + MediaLifecyclePolicy.TombstoneRetention)
                    .SetProperty(media => media.StatusChangedAt, now)
                    .SetProperty(media => media.UpdatedAt, now),
                cancellationToken);
    }

    private static DateTimeOffset PostgreSqlTimestamp(DateTimeOffset value) =>
        value.AddTicks(-(value.Ticks % TimeSpan.TicksPerMicrosecond));
}
