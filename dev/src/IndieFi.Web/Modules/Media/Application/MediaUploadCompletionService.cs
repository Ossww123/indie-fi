using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Media.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Media.Application;

public enum MediaUploadCompletionOutcome
{
    Completed,
    AlreadyCompleted,
    UploadRejected,
    AccountUnavailable,
    MediaNotFound,
    UploadAttemptNotFound,
    UploadAttemptSuperseded,
    MediaNotPending,
    ReservationExpired,
    StagingObjectNotFound,
    StagingObjectChanged,
    StorageResponseInvalid,
    SourceObjectUnavailable,
    SourceObjectConflict,
}

public sealed record MediaUploadCompletionResult(
    MediaUploadCompletionOutcome Outcome,
    Guid? ProcessingEventId = null);

public sealed class MediaUploadCompletionService
{
    public const string UploadMetadataMismatchFailureCode =
        "upload_metadata_mismatch";

    private readonly AppDbContext _db;
    private readonly IMediaObjectStorage _storage;
    private readonly IMediaProcessingPublisher _processingPublisher;
    private readonly TimeProvider _timeProvider;

    public MediaUploadCompletionService(
        AppDbContext db,
        IMediaObjectStorage storage,
        IMediaProcessingPublisher processingPublisher,
        TimeProvider timeProvider)
    {
        _db = db;
        _storage = storage;
        _processingPublisher = processingPublisher;
        _timeProvider = timeProvider;
    }

    public async Task<MediaUploadCompletionResult> CompleteAsync(
        long ownerUserId,
        Guid mediaId,
        Guid uploadAttemptId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ownerUserId);
        RequireIdentifier(mediaId, nameof(mediaId));
        RequireIdentifier(uploadAttemptId, nameof(uploadAttemptId));

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
            return new MediaUploadCompletionResult(
                MediaUploadCompletionOutcome.AccountUnavailable);
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
            return new MediaUploadCompletionResult(
                MediaUploadCompletionOutcome.MediaNotFound);
        }

        var attempt = await _db.MediaUploadAttempts.SingleOrDefaultAsync(
            item => item.Id == uploadAttemptId && item.MediaId == mediaId,
            cancellationToken);
        if (attempt is null)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new MediaUploadCompletionResult(
                MediaUploadCompletionOutcome.UploadAttemptNotFound);
        }

        if (attempt.SupersededAt is not null)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new MediaUploadCompletionResult(
                MediaUploadCompletionOutcome.UploadAttemptSuperseded);
        }

        var processingEventId = MediaProcessingEventId.Create(
            media.Id,
            media.Revision);
        if (attempt.CompletedAt is not null)
        {
            var repeatedOutcome = IsCompletedState(media.Status)
                ? MediaUploadCompletionOutcome.AlreadyCompleted
                : media.Status == MediaStatus.Failed
                    && media.FailureCode == UploadMetadataMismatchFailureCode
                    ? MediaUploadCompletionOutcome.UploadRejected
                    : MediaUploadCompletionOutcome.MediaNotPending;
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new MediaUploadCompletionResult(
                repeatedOutcome,
                repeatedOutcome == MediaUploadCompletionOutcome.AlreadyCompleted
                    ? processingEventId
                    : null);
        }

        if (media.Status != MediaStatus.UploadPending)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new MediaUploadCompletionResult(
                MediaUploadCompletionOutcome.MediaNotPending);
        }

        var now = PostgreSqlTimestamp(_timeProvider.GetUtcNow());
        if (media.UploadExpiresAt <= now)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new MediaUploadCompletionResult(
                MediaUploadCompletionOutcome.ReservationExpired);
        }

        var sourceKey = MediaObjectKey.Source(media.Id, media.Revision);
        var sourceMetadata = await _storage.GetMetadataAsync(
            sourceKey,
            cancellationToken);
        if (sourceMetadata is null)
        {
            var stagingKey = MediaObjectKey.Staging(media.Id, uploadAttemptId);
            var stagingMetadata = await _storage.GetMetadataAsync(
                stagingKey,
                cancellationToken);
            if (stagingMetadata is null)
            {
                await AccountMutationGuard.CommitIfOwnedAsync(
                    transaction,
                    cancellationToken);
                return new MediaUploadCompletionResult(
                    MediaUploadCompletionOutcome.StagingObjectNotFound);
            }

            if (!MediaObjectAccessPolicy.IsValidETag(stagingMetadata.ETag))
            {
                await AccountMutationGuard.CommitIfOwnedAsync(
                    transaction,
                    cancellationToken);
                return new MediaUploadCompletionResult(
                    MediaUploadCompletionOutcome.StorageResponseInvalid);
            }

            if (!MatchesDeclaration(media, stagingMetadata))
            {
                await RejectUploadAsync(media, attempt, now, cancellationToken);
                await AccountMutationGuard.CommitIfOwnedAsync(
                    transaction,
                    cancellationToken);
                return new MediaUploadCompletionResult(
                    MediaUploadCompletionOutcome.UploadRejected);
            }

            try
            {
                await _storage.CopyAsync(
                    MediaObjectAccessPolicy.CreateSourceCopyRequest(
                        stagingKey,
                        sourceKey,
                        stagingMetadata.ETag,
                        media.DeclaredContentType),
                    cancellationToken);
            }
            catch (MediaObjectStorageException exception)
                when (exception.FailureCode ==
                    MediaObjectStorageFailureCode.PreconditionFailed)
            {
                await AccountMutationGuard.CommitIfOwnedAsync(
                    transaction,
                    cancellationToken);
                return new MediaUploadCompletionResult(
                    MediaUploadCompletionOutcome.StagingObjectChanged);
            }
            catch (MediaObjectStorageException exception)
                when (exception.FailureCode ==
                    MediaObjectStorageFailureCode.ObjectNotFound)
            {
                await AccountMutationGuard.CommitIfOwnedAsync(
                    transaction,
                    cancellationToken);
                return new MediaUploadCompletionResult(
                    MediaUploadCompletionOutcome.StagingObjectNotFound);
            }

            sourceMetadata = await _storage.GetMetadataAsync(
                sourceKey,
                cancellationToken);
            if (sourceMetadata is null)
            {
                await AccountMutationGuard.CommitIfOwnedAsync(
                    transaction,
                    cancellationToken);
                return new MediaUploadCompletionResult(
                    MediaUploadCompletionOutcome.SourceObjectUnavailable);
            }
        }

        if (!MatchesDeclaration(media, sourceMetadata)
            || !MediaObjectAccessPolicy.IsValidETag(sourceMetadata.ETag))
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new MediaUploadCompletionResult(
                MediaUploadCompletionOutcome.SourceObjectConflict);
        }

        media.SourceContentType = sourceMetadata.ContentType;
        media.SourceBytes = sourceMetadata.ContentLength;
        media.SourceETag = sourceMetadata.ETag;
        media.Status = MediaStatus.ProcessingPending;
        media.StatusChangedAt = now;
        media.UpdatedAt = now;
        attempt.CompletedAt = now;

        await _processingPublisher.StageAsync(
            processingEventId,
            media.Id,
            media.Revision,
            now,
            cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await AccountMutationGuard.CommitIfOwnedAsync(transaction, cancellationToken);

        return new MediaUploadCompletionResult(
            MediaUploadCompletionOutcome.Completed,
            processingEventId);
    }

    private async Task RejectUploadAsync(
        MediaAsset media,
        MediaUploadAttempt attempt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        media.Status = MediaStatus.Failed;
        media.FailureCode = UploadMetadataMismatchFailureCode;
        media.StatusChangedAt = now;
        media.UpdatedAt = now;
        attempt.CompletedAt = now;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static bool MatchesDeclaration(
        MediaAsset media,
        ObjectStorageMetadata metadata) =>
        metadata.ContentLength == media.DeclaredBytes
        && string.Equals(
            metadata.ContentType,
            media.DeclaredContentType,
            StringComparison.Ordinal);

    private static bool IsCompletedState(MediaStatus status) => status is
        MediaStatus.ProcessingPending
        or MediaStatus.Processing
        or MediaStatus.Ready;

    private static DateTimeOffset PostgreSqlTimestamp(DateTimeOffset value) =>
        value.AddTicks(-(value.Ticks % TimeSpan.TicksPerMicrosecond));

    private static void RequireIdentifier(Guid identifier, string parameterName)
    {
        if (identifier == Guid.Empty)
        {
            throw new ArgumentException(
                "A media upload identifier must not be empty.",
                parameterName);
        }
    }
}
