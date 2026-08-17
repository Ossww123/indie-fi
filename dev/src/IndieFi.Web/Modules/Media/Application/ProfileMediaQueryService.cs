using IndieFi.Web.Data;
using IndieFi.Web.Modules.Media.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Media.Application;

public sealed record OwnerProfileMediaItem(
    Guid MediaId,
    MediaStatus Status,
    bool IsCurrent,
    long DeclaredBytes,
    int ProcessingAttemptCount,
    string? FailureCode,
    DateTimeOffset UploadExpiresAt,
    DateTimeOffset? PublishedAt,
    DateTimeOffset UpdatedAt,
    Uri? PreviewUrl);

public sealed record OwnerProfileMedia(
    long UserId,
    IReadOnlyList<OwnerProfileMediaItem> Images);

public sealed record PublicProfileMediaImage(Guid MediaId, Uri Url);

public sealed record PublicProfileMedia(PublicProfileMediaImage? Image);

public sealed class ProfileMediaQueryService
{
    private static readonly MediaStatus[] OwnerVisibleStatuses =
    [
        MediaStatus.UploadPending,
        MediaStatus.ProcessingPending,
        MediaStatus.Processing,
        MediaStatus.Ready,
        MediaStatus.Failed,
        MediaStatus.Expired,
    ];

    private readonly AppDbContext _db;
    private readonly IMediaObjectStorage _storage;

    public ProfileMediaQueryService(
        AppDbContext db,
        IMediaObjectStorage storage)
    {
        _db = db;
        _storage = storage;
    }

    public async Task<OwnerProfileMedia?> GetOwnerAsync(
        long ownerUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ownerUserId);

        if (!await UserExistsAsync(ownerUserId, cancellationToken))
        {
            return null;
        }

        var snapshots = await CurrentProfileMedia(ownerUserId)
            .Where(media => OwnerVisibleStatuses.Contains(media.Status))
            .Select(media => new MediaSnapshot(
                media.Id,
                media.Revision,
                media.Status,
                media.IsCurrent,
                media.DeclaredBytes,
                media.ProcessingAttemptCount,
                media.FailureCode,
                media.UploadExpiresAt,
                media.PublishedAt,
                media.CreatedAt,
                media.UpdatedAt))
            .ToArrayAsync(cancellationToken);
        var images = new List<OwnerProfileMediaItem>(snapshots.Length);
        foreach (var snapshot in snapshots
                     .OrderBy(item => item.Status == MediaStatus.Ready ? 0 : 1)
                     .ThenBy(item => item.Status)
                     .ThenByDescending(item => item.CreatedAt)
                     .ThenBy(item => item.MediaId))
        {
            var previewUrl = snapshot.Status == MediaStatus.Ready
                ? await CreateDownloadUrlAsync(
                    snapshot.MediaId,
                    snapshot.Revision,
                    MediaDerivedVariant.ProfileThumbnail,
                    cancellationToken)
                : null;
            images.Add(new OwnerProfileMediaItem(
                snapshot.MediaId,
                snapshot.Status,
                snapshot.IsCurrent,
                snapshot.DeclaredBytes,
                snapshot.ProcessingAttemptCount,
                snapshot.FailureCode,
                snapshot.UploadExpiresAt,
                snapshot.PublishedAt,
                snapshot.UpdatedAt,
                previewUrl));
        }

        return new OwnerProfileMedia(ownerUserId, images);
    }

    public async Task<PublicProfileMedia?> GetPublicAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);

        if (!await UserExistsAsync(userId, cancellationToken))
        {
            return null;
        }

        var current = await CurrentProfileMedia(userId)
            .Where(media => media.Status == MediaStatus.Ready
                && media.IsCurrent)
            .Select(media => new PublicSnapshot(media.Id, media.Revision))
            .SingleOrDefaultAsync(cancellationToken);
        if (current is null)
        {
            return new PublicProfileMedia(null);
        }

        return new PublicProfileMedia(new PublicProfileMediaImage(
            current.MediaId,
            await CreateDownloadUrlAsync(
                current.MediaId,
                current.Revision,
                MediaDerivedVariant.ProfileDisplay,
                cancellationToken)));
    }

    private Task<bool> UserExistsAsync(
        long userId,
        CancellationToken cancellationToken) =>
        _db.Users
            .AsNoTracking()
            .AnyAsync(user => user.Id == userId, cancellationToken);

    private IQueryable<MediaAsset> CurrentProfileMedia(long userId) =>
        _db.MediaAssets
            .AsNoTracking()
            .Where(media => media.ProfileUserId == userId
                && media.OwnerUserId == userId
                && media.AttachmentKind == MediaAttachmentKind.ProfileImage);

    private Task<Uri> CreateDownloadUrlAsync(
        Guid mediaId,
        int revision,
        MediaDerivedVariant variant,
        CancellationToken cancellationToken) =>
        _storage.CreateDownloadUrlAsync(
            MediaObjectAccessPolicy.CreateDownloadUrlRequest(
                MediaObjectKey.Derived(mediaId, revision, variant)),
            cancellationToken);

    private sealed record MediaSnapshot(
        Guid MediaId,
        int Revision,
        MediaStatus Status,
        bool IsCurrent,
        long DeclaredBytes,
        int ProcessingAttemptCount,
        string? FailureCode,
        DateTimeOffset UploadExpiresAt,
        DateTimeOffset? PublishedAt,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    private sealed record PublicSnapshot(Guid MediaId, int Revision);
}
