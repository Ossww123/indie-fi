using IndieFi.Web.Modules.Media.Domain;

namespace IndieFi.Web.Modules.Media.Application;

public sealed class MediaObjectDeletionService
{
    private readonly IMediaObjectStorage _storage;

    public MediaObjectDeletionService(IMediaObjectStorage storage)
    {
        _storage = storage;
    }

    public async Task DeleteStagingAndSourceAsync(
        Guid mediaId,
        int revision,
        IReadOnlyCollection<Guid> uploadAttemptIds,
        CancellationToken cancellationToken = default)
    {
        foreach (var uploadAttemptId in uploadAttemptIds)
        {
            await _storage.DeleteIfExistsAsync(
                MediaObjectKey.Staging(mediaId, uploadAttemptId),
                cancellationToken);
        }

        await _storage.DeleteIfExistsAsync(
            MediaObjectKey.Source(mediaId, revision),
            cancellationToken);
    }

    public async Task DeleteAllAsync(
        Guid mediaId,
        int revision,
        IReadOnlyCollection<Guid> uploadAttemptIds,
        CancellationToken cancellationToken = default)
    {
        await DeleteStagingAndSourceAsync(
            mediaId,
            revision,
            uploadAttemptIds,
            cancellationToken);
        foreach (var variant in Enum.GetValues<MediaDerivedVariant>())
        {
            await _storage.DeleteIfExistsAsync(
                MediaObjectKey.Derived(mediaId, revision, variant),
                cancellationToken);
        }
    }
}
