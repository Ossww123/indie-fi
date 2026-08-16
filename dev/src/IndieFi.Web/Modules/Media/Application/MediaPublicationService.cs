using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Media.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Media.Application;

public enum MediaPublishReadyOutcome
{
    Published,
    AlreadyReady,
    MediaNotFound,
    AccountUnavailable,
    NotProcessing,
    ProcessingLeaseLost,
    ValidationIncomplete,
    ScreenshotLimitReached,
}

public sealed record MediaPublishReadyResult(
    MediaPublishReadyOutcome Outcome,
    Guid? ReplacedMediaDeletionEventId = null,
    int? ScreenshotSortOrder = null);

public enum MediaRemovalOutcome
{
    Removed,
    AlreadyRemoved,
    AccountUnavailable,
    MediaNotFound,
    NotRemovable,
}

public sealed record MediaRemovalResult(
    MediaRemovalOutcome Outcome,
    Guid? DeletionEventId = null);

public sealed class MediaPublicationService
{
    private readonly AppDbContext _db;
    private readonly IMediaDeletionPublisher _deletionPublisher;
    private readonly TimeProvider _timeProvider;

    public MediaPublicationService(
        AppDbContext db,
        IMediaDeletionPublisher deletionPublisher,
        TimeProvider timeProvider)
    {
        _db = db;
        _deletionPublisher = deletionPublisher;
        _timeProvider = timeProvider;
    }

    public async Task<MediaPublishReadyResult> PublishReadyAsync(
        Guid mediaId,
        Guid processingLeaseId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(mediaId, nameof(mediaId));
        RequireIdentifier(processingLeaseId, nameof(processingLeaseId));

        var snapshot = await FindTargetSnapshotAsync(mediaId, cancellationToken);
        if (snapshot is null)
        {
            return new MediaPublishReadyResult(MediaPublishReadyOutcome.MediaNotFound);
        }

        await using var transaction =
            await AccountMutationGuard.BeginTransactionIfNeededAsync(
                _db,
                cancellationToken);
        if (!await AccountMutationGuard.LockActiveAsync(
                _db,
                snapshot.OwnerUserId,
                cancellationToken))
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new MediaPublishReadyResult(
                MediaPublishReadyOutcome.AccountUnavailable);
        }

        var target = snapshot.ToTarget();
        if (!await LockOwnedTargetAsync(
                snapshot.OwnerUserId,
                target,
                cancellationToken))
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new MediaPublishReadyResult(MediaPublishReadyOutcome.MediaNotFound);
        }

        var mediaRows = await _db.MediaAssets
            .FromSqlInterpolated(
                $"SELECT * FROM media_assets WHERE id = {mediaId} AND owner_user_id = {snapshot.OwnerUserId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        var media = mediaRows.SingleOrDefault();
        if (media is null)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new MediaPublishReadyResult(MediaPublishReadyOutcome.MediaNotFound);
        }

        if (media.Status == MediaStatus.Ready)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new MediaPublishReadyResult(
                MediaPublishReadyOutcome.AlreadyReady,
                ScreenshotSortOrder: media.SortOrder);
        }

        if (media.Status != MediaStatus.Processing)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new MediaPublishReadyResult(MediaPublishReadyOutcome.NotProcessing);
        }

        var now = PostgreSqlTimestamp(_timeProvider.GetUtcNow());
        if (media.ProcessingLeaseId != processingLeaseId
            || media.ProcessingLeaseExpiresAt is null
            || media.ProcessingLeaseExpiresAt <= now)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new MediaPublishReadyResult(
                MediaPublishReadyOutcome.ProcessingLeaseLost);
        }

        if (!HasCompleteValidation(media))
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new MediaPublishReadyResult(
                MediaPublishReadyOutcome.ValidationIncomplete);
        }

        Guid? replacedDeletionEventId = null;
        if (MediaPolicy.UsesSingleCurrentSlot(media.AttachmentKind))
        {
            var replaced = await TargetMedia(target).SingleOrDefaultAsync(
                item => item.AttachmentKind == media.AttachmentKind
                    && item.Status == MediaStatus.Ready,
                cancellationToken);
            if (replaced is not null)
            {
                replacedDeletionEventId = MediaDeletionEventId.Create(
                    replaced.Id,
                    replaced.Revision);
                DisconnectForDeletion(replaced, now);
                await _deletionPublisher.StageAsync(
                    replacedDeletionEventId.Value,
                    replaced.Id,
                    replaced.Revision,
                    now,
                    cancellationToken);

                // Release the singleton partial unique index before the candidate
                // becomes Ready. Both saves remain inside the same transaction.
                await _db.SaveChangesAsync(cancellationToken);
            }
        }

        int? screenshotSortOrder = null;
        if (media.AttachmentKind == MediaAttachmentKind.GameScreenshot)
        {
            var usedOrders = await TargetMedia(target)
                .Where(item => item.AttachmentKind == MediaAttachmentKind.GameScreenshot
                    && item.Status == MediaStatus.Ready)
                .Select(item => item.SortOrder!.Value)
                .ToListAsync(cancellationToken);
            screenshotSortOrder = Enumerable
                .Range(0, MediaPolicy.GameScreenshotMaximumCount)
                .FirstOrDefault(
                    order => !usedOrders.Contains(order),
                    defaultValue: -1);
            if (screenshotSortOrder < 0)
            {
                await AccountMutationGuard.RollbackIfOwnedAsync(
                    transaction,
                    cancellationToken);
                return new MediaPublishReadyResult(
                    MediaPublishReadyOutcome.ScreenshotLimitReached);
            }
        }

        media.Status = MediaStatus.Ready;
        media.ProcessingLeaseId = null;
        media.ProcessingLeaseExpiresAt = null;
        media.NextProcessingAttemptAt = null;
        media.IsCurrent = true;
        media.SortOrder = screenshotSortOrder;
        media.PublishedAt = now;
        media.StatusChangedAt = now;
        media.UpdatedAt = now;
        await _db.SaveChangesAsync(cancellationToken);
        await AccountMutationGuard.CommitIfOwnedAsync(transaction, cancellationToken);

        return new MediaPublishReadyResult(
            MediaPublishReadyOutcome.Published,
            replacedDeletionEventId,
            screenshotSortOrder);
    }

    public async Task<MediaRemovalResult> RemoveAsync(
        long ownerUserId,
        Guid mediaId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ownerUserId);
        RequireIdentifier(mediaId, nameof(mediaId));

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
            return new MediaRemovalResult(MediaRemovalOutcome.AccountUnavailable);
        }

        var snapshot = await FindTargetSnapshotAsync(
            mediaId,
            ownerUserId,
            cancellationToken);
        if (snapshot is null)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new MediaRemovalResult(MediaRemovalOutcome.MediaNotFound);
        }

        var target = snapshot.ToTarget();
        if (!await LockOwnedTargetAsync(ownerUserId, target, cancellationToken))
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new MediaRemovalResult(MediaRemovalOutcome.MediaNotFound);
        }

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
            return new MediaRemovalResult(MediaRemovalOutcome.MediaNotFound);
        }

        var deletionEventId = MediaDeletionEventId.Create(media.Id, media.Revision);
        if (media.Status is MediaStatus.DeletePending or MediaStatus.Deleted)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new MediaRemovalResult(
                MediaRemovalOutcome.AlreadyRemoved,
                deletionEventId);
        }

        if (!MediaLifecyclePolicy.CanTransition(
                media.Status,
                MediaStatus.DeletePending))
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new MediaRemovalResult(MediaRemovalOutcome.NotRemovable);
        }

        var now = PostgreSqlTimestamp(_timeProvider.GetUtcNow());
        DisconnectForDeletion(media, now);
        await _deletionPublisher.StageAsync(
            deletionEventId,
            media.Id,
            media.Revision,
            now,
            cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await AccountMutationGuard.CommitIfOwnedAsync(transaction, cancellationToken);

        return new MediaRemovalResult(
            MediaRemovalOutcome.Removed,
            deletionEventId);
    }

    private async Task<MediaTargetSnapshot?> FindTargetSnapshotAsync(
        Guid mediaId,
        CancellationToken cancellationToken) =>
        await FindTargetSnapshotAsync(
            mediaId,
            ownerUserId: null,
            cancellationToken);

    private async Task<MediaTargetSnapshot?> FindTargetSnapshotAsync(
        Guid mediaId,
        long? ownerUserId,
        CancellationToken cancellationToken)
    {
        var query = _db.MediaAssets
            .AsNoTracking()
            .Where(item => item.Id == mediaId);
        if (ownerUserId is not null)
        {
            query = query.Where(item => item.OwnerUserId == ownerUserId.Value);
        }

        return await query
            .Select(item => new MediaTargetSnapshot(
                item.OwnerUserId,
                item.GameId,
                item.ProfileUserId))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private IQueryable<MediaAsset> TargetMedia(MediaTarget target) =>
        target.Kind switch
        {
            MediaTargetKind.Game => _db.MediaAssets.Where(
                item => item.GameId == target.Id),
            MediaTargetKind.Profile => _db.MediaAssets.Where(
                item => item.ProfileUserId == target.Id),
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };

    private async Task<bool> LockOwnedTargetAsync(
        long ownerUserId,
        MediaTarget target,
        CancellationToken cancellationToken)
    {
        if (target.Kind == MediaTargetKind.Profile)
        {
            if (target.Id != ownerUserId)
            {
                return false;
            }

            var profiles = await _db.UserProfiles
                .FromSqlInterpolated(
                    $"SELECT * FROM user_profiles WHERE user_id = {target.Id} FOR UPDATE")
                .AsNoTracking()
                .ToListAsync(cancellationToken);
            return profiles.Count == 1;
        }

        if (target.Kind == MediaTargetKind.Game)
        {
            var games = await _db.Games
                .FromSqlInterpolated(
                    $"SELECT * FROM games WHERE id = {target.Id} AND owner_id = {ownerUserId} FOR UPDATE")
                .AsNoTracking()
                .ToListAsync(cancellationToken);
            return games.Count == 1;
        }

        throw new ArgumentOutOfRangeException(nameof(target));
    }

    private static bool HasCompleteValidation(MediaAsset media) =>
        media.SourceContentType is not null
        && media.SourceBytes is not null
        && media.SourceETag is not null
        && media.DetectedContentType is not null
        && media.PixelWidth is not null
        && media.PixelHeight is not null
        && media.FrameCount == 1;

    private static void DisconnectForDeletion(
        MediaAsset media,
        DateTimeOffset now)
    {
        media.Status = MediaStatus.DeletePending;
        media.ProcessingLeaseId = null;
        media.ProcessingLeaseExpiresAt = null;
        media.NextProcessingAttemptAt = null;
        media.IsCurrent = false;
        media.SortOrder = null;
        media.PublishedAt = null;
        media.StatusChangedAt = now;
        media.UpdatedAt = now;
    }

    private static DateTimeOffset PostgreSqlTimestamp(DateTimeOffset value) =>
        value.AddTicks(-(value.Ticks % TimeSpan.TicksPerMicrosecond));

    private static void RequireIdentifier(Guid identifier, string parameterName)
    {
        if (identifier == Guid.Empty)
        {
            throw new ArgumentException(
                "A media identifier must not be empty.",
                parameterName);
        }
    }

    private sealed record MediaTargetSnapshot(
        long OwnerUserId,
        long? GameId,
        long? ProfileUserId)
    {
        public MediaTarget ToTarget() => GameId is not null
            ? MediaTarget.Game(GameId.Value)
            : MediaTarget.Profile(ProfileUserId!.Value);
    }
}
