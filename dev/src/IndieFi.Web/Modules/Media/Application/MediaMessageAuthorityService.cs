using IndieFi.Web.Data;
using IndieFi.Web.Modules.Media.Contracts;
using IndieFi.Web.Modules.Media.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Media.Application;

public enum MediaMessageAuthorityOutcome
{
    Accepted,
    InvalidContract,
    MediaNotFound,
    RevisionMismatch,
    StatusMismatch,
}

public sealed class MediaMessageAuthorityService
{
    private readonly AppDbContext _db;

    public MediaMessageAuthorityService(AppDbContext db)
    {
        _db = db;
    }

    public Task<MediaMessageAuthorityOutcome> AuthorizeProcessingAsync(
        Guid? transportMessageId,
        MediaProcessingRequestedV1 message,
        CancellationToken cancellationToken = default)
    {
        var structurallyValid = HasValidEnvelope(
                transportMessageId,
                message.EventId,
                message.MediaId,
                message.Revision,
                message.OccurredAt)
            && message.EventId == MediaProcessingEventId.Create(
                message.MediaId,
                message.Revision);
        return structurallyValid
            ? AuthorizeCurrentStateAsync(
                message.MediaId,
                message.Revision,
                MediaStatus.ProcessingPending,
                cancellationToken)
            : Task.FromResult(MediaMessageAuthorityOutcome.InvalidContract);
    }

    public Task<MediaMessageAuthorityOutcome> AuthorizeDeletionAsync(
        Guid? transportMessageId,
        MediaDeletionRequestedV1 message,
        CancellationToken cancellationToken = default)
    {
        var structurallyValid = HasValidEnvelope(
                transportMessageId,
                message.EventId,
                message.MediaId,
                message.Revision,
                message.OccurredAt)
            && message.EventId == MediaDeletionEventId.Create(
                message.MediaId,
                message.Revision);
        return structurallyValid
            ? AuthorizeCurrentStateAsync(
                message.MediaId,
                message.Revision,
                MediaStatus.DeletePending,
                cancellationToken)
            : Task.FromResult(MediaMessageAuthorityOutcome.InvalidContract);
    }

    private async Task<MediaMessageAuthorityOutcome> AuthorizeCurrentStateAsync(
        Guid mediaId,
        int revision,
        MediaStatus requiredStatus,
        CancellationToken cancellationToken)
    {
        var state = await _db.MediaAssets
            .AsNoTracking()
            .Where(media => media.Id == mediaId)
            .Select(media => new MediaState(media.Revision, media.Status))
            .SingleOrDefaultAsync(cancellationToken);
        if (state is null)
        {
            return MediaMessageAuthorityOutcome.MediaNotFound;
        }

        if (state.Revision != revision)
        {
            return MediaMessageAuthorityOutcome.RevisionMismatch;
        }

        return state.Status == requiredStatus
            ? MediaMessageAuthorityOutcome.Accepted
            : MediaMessageAuthorityOutcome.StatusMismatch;
    }

    private static bool HasValidEnvelope(
        Guid? transportMessageId,
        Guid eventId,
        Guid mediaId,
        int revision,
        DateTimeOffset occurredAt) =>
        transportMessageId.HasValue
        && transportMessageId.Value != Guid.Empty
        && transportMessageId.Value == eventId
        && eventId != Guid.Empty
        && mediaId != Guid.Empty
        && revision > 0
        && occurredAt != default;

    private sealed record MediaState(int Revision, MediaStatus Status);
}
