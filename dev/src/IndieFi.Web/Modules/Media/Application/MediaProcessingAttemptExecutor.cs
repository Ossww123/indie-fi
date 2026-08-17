using IndieFi.Web.Data;
using IndieFi.Web.Modules.Media.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Media.Application;

public sealed class MediaProcessingAttemptExecutor : IMediaProcessingAttemptExecutor
{
    public const string SourceMetadataMissingFailureCode =
        "source_metadata_missing";
    public const string SourceObjectMissingFailureCode =
        "source_object_missing";
    public const string SourceObjectChangedFailureCode =
        "source_object_changed";

    private readonly AppDbContext _db;
    private readonly IMediaObjectStorage _storage;
    private readonly IMediaImageProcessor _imageProcessor;
    private readonly IMediaImageDerivativeEncoder _derivativeEncoder;
    private readonly MediaPublicationService _publication;

    public MediaProcessingAttemptExecutor(
        AppDbContext db,
        IMediaObjectStorage storage,
        IMediaImageProcessor imageProcessor,
        IMediaImageDerivativeEncoder derivativeEncoder,
        MediaPublicationService publication)
    {
        _db = db;
        _storage = storage;
        _imageProcessor = imageProcessor;
        _derivativeEncoder = derivativeEncoder;
        _publication = publication;
    }

    public async Task<MediaProcessingAttemptResult> ExecuteAsync(
        MediaProcessingLease lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var media = await _db.MediaAssets
            .AsNoTracking()
            .Where(item => item.Id == lease.MediaId
                && item.Revision == lease.Revision
                && item.Status == MediaStatus.Processing
                && item.ProcessingLeaseId == lease.LeaseId
                && item.ProcessingAttemptCount == lease.AttemptNumber)
            .Select(item => new ProcessingMediaSnapshot(
                item.Id,
                item.Revision,
                item.AttachmentKind,
                item.SourceContentType,
                item.SourceBytes,
                item.SourceETag))
            .SingleOrDefaultAsync(cancellationToken);
        if (media is null)
        {
            return MediaProcessingAttemptResult.Completed();
        }

        if (media.SourceContentType is null
            || media.SourceBytes is null
            || media.SourceETag is null)
        {
            return PermanentFailure(SourceMetadataMissingFailureCode);
        }

        ObjectStorageReadResult source;
        try
        {
            source = await _storage.OpenReadAsync(
                MediaObjectKey.Source(media.Id, media.Revision),
                cancellationToken);
        }
        catch (MediaObjectStorageException exception)
            when (exception.FailureCode ==
                MediaObjectStorageFailureCode.ObjectNotFound)
        {
            return PermanentFailure(SourceObjectMissingFailureCode);
        }
        catch (MediaObjectStorageException exception)
        {
            return StorageFailure(exception);
        }

        try
        {
            await using (source)
            {
                return await ProcessSourceAsync(
                    lease,
                    media,
                    source,
                    cancellationToken);
            }
        }
        catch (MediaObjectStorageException exception)
        {
            return StorageFailure(exception);
        }
    }

    private async Task<MediaProcessingAttemptResult> ProcessSourceAsync(
        MediaProcessingLease lease,
        ProcessingMediaSnapshot media,
        ObjectStorageReadResult source,
        CancellationToken cancellationToken)
    {
        if (source.Metadata.ContentLength != media.SourceBytes
            || !string.Equals(
                source.Metadata.ContentType,
                media.SourceContentType,
                StringComparison.Ordinal)
            || !string.Equals(
                source.Metadata.ETag,
                media.SourceETag,
                StringComparison.Ordinal))
        {
            return PermanentFailure(SourceObjectChangedFailureCode);
        }

        var processing = await _imageProcessor.ProcessAsync(
            source.Content,
            source.Metadata.ContentLength,
            media.AttachmentKind,
            cancellationToken);
        if (!processing.Succeeded)
        {
            return PermanentFailure(processing.FailureCode!);
        }

        using var image = processing.Image!;
        var derivatives = _derivativeEncoder.Encode(
            image,
            media.AttachmentKind);
        foreach (var derivative in derivatives)
        {
            await using var content = derivative.OpenRead();
            await _storage.WriteAsync(
                MediaObjectAccessPolicy.CreateDerivedWriteRequest(
                    MediaObjectKey.Derived(
                        media.Id,
                        media.Revision,
                        derivative.Variant),
                    content,
                    derivative.ContentLength),
                cancellationToken);
        }

        var publication = await _publication.PublishReadyAsync(
            media.Id,
            lease.LeaseId,
            image.Validation,
            cancellationToken);
        return publication.Outcome switch
        {
            MediaPublishReadyOutcome.Published or
                MediaPublishReadyOutcome.AlreadyReady or
                MediaPublishReadyOutcome.MediaNotFound or
                MediaPublishReadyOutcome.NotProcessing or
                MediaPublishReadyOutcome.ProcessingLeaseLost =>
                MediaProcessingAttemptResult.Completed(),
            MediaPublishReadyOutcome.AccountUnavailable =>
                PermanentFailure("media_owner_unavailable"),
            MediaPublishReadyOutcome.ScreenshotLimitReached =>
                PermanentFailure("media_screenshot_limit"),
            MediaPublishReadyOutcome.ValidationIncomplete =>
                MediaProcessingAttemptResult.TransientFailure(
                    "processing_validation_incomplete"),
            _ => throw new InvalidOperationException(
                $"Unknown media publication outcome: {publication.Outcome}."),
        };
    }

    private static MediaProcessingAttemptResult StorageFailure(
        MediaObjectStorageException exception) =>
        MediaProcessingAttemptResult.TransientFailure(
            exception.FailureCode switch
            {
                MediaObjectStorageFailureCode.ObjectNotFound =>
                    "storage_object_not_found",
                MediaObjectStorageFailureCode.PreconditionFailed =>
                    "storage_precondition_failed",
                MediaObjectStorageFailureCode.AccessDenied =>
                    "storage_access_denied",
                MediaObjectStorageFailureCode.TimedOut =>
                    "storage_timed_out",
                MediaObjectStorageFailureCode.Unavailable =>
                    "storage_unavailable",
                MediaObjectStorageFailureCode.InvalidResponse =>
                    "storage_invalid_response",
                _ => "storage_unexpected_error",
            });

    private static MediaProcessingAttemptResult PermanentFailure(
        string failureCode) =>
        MediaProcessingAttemptResult.PermanentFailure(failureCode);

    private sealed record ProcessingMediaSnapshot(
        Guid Id,
        int Revision,
        MediaAttachmentKind AttachmentKind,
        string? SourceContentType,
        long? SourceBytes,
        string? SourceETag);
}
