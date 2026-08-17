using IndieFi.Web.Data;
using IndieFi.Web.Modules.Media.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Media.Application;

public sealed record MediaOperationalSnapshot(
    int ProcessingPendingCount,
    int RecoveryDueCount,
    int ActiveLeaseCount,
    int ExpiredLeaseCount,
    int CleanupBacklogCount,
    int FailedCount,
    double OldestRecoveryDueAgeSeconds);

public sealed class MediaOperationalSnapshotService
{
    private readonly AppDbContext _db;

    public MediaOperationalSnapshotService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<MediaOperationalSnapshot> GetAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var media = _db.MediaAssets.AsNoTracking();
        var processingPendingCount = await media.CountAsync(
            item => item.Status == MediaStatus.ProcessingPending,
            cancellationToken);
        var recoveryDue = media
            .Where(item =>
                (item.Status == MediaStatus.ProcessingPending
                    && (item.NextProcessingAttemptAt == null
                        || item.NextProcessingAttemptAt <= now))
                || (item.Status == MediaStatus.Processing
                    && item.ProcessingLeaseExpiresAt <= now))
            .Select(item => item.Status == MediaStatus.ProcessingPending
                ? item.NextProcessingAttemptAt ?? item.UpdatedAt
                : item.ProcessingLeaseExpiresAt!.Value);
        var recoveryDueCount = await recoveryDue.CountAsync(cancellationToken);
        var oldestRecoveryDueAt = await recoveryDue
            .MinAsync(value => (DateTimeOffset?)value, cancellationToken);
        var activeLeaseCount = await media.CountAsync(
            item => item.Status == MediaStatus.Processing
                && item.ProcessingLeaseExpiresAt > now,
            cancellationToken);
        var expiredLeaseCount = await media.CountAsync(
            item => item.Status == MediaStatus.Processing
                && item.ProcessingLeaseExpiresAt <= now,
            cancellationToken);
        var cleanupBacklogCount = await media.CountAsync(
            item =>
                (item.Status == MediaStatus.UploadPending
                    && item.UploadExpiresAt <= now)
                || item.Status == MediaStatus.DeletePending
                || (item.Status == MediaStatus.Ready
                    && item.SourceDeletedAt == null)
                || ((item.Status == MediaStatus.Failed
                        || item.Status == MediaStatus.Expired
                        || item.Status == MediaStatus.Deleted)
                    && (item.DeletionCompletedAt == null
                        || item.TombstoneExpiresAt <= now)),
            cancellationToken);
        var failedCount = await media.CountAsync(
            item => item.Status == MediaStatus.Failed,
            cancellationToken);

        return new MediaOperationalSnapshot(
            processingPendingCount,
            recoveryDueCount,
            activeLeaseCount,
            expiredLeaseCount,
            cleanupBacklogCount,
            failedCount,
            AgeSeconds(now, oldestRecoveryDueAt));
    }

    private static double AgeSeconds(
        DateTimeOffset now,
        DateTimeOffset? value) => value is null
            ? 0
            : Math.Max(0, (now - value.Value).TotalSeconds);
}
