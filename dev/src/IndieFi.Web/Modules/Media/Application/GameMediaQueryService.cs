using IndieFi.Web.Data;
using IndieFi.Web.Modules.Media.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Media.Application;

public sealed record OwnerGameMediaItem(
    Guid MediaId,
    MediaAttachmentKind AttachmentKind,
    MediaStatus Status,
    bool IsCurrent,
    int? SortOrder,
    long DeclaredBytes,
    int ProcessingAttemptCount,
    string? FailureCode,
    DateTimeOffset UploadExpiresAt,
    DateTimeOffset? PublishedAt,
    DateTimeOffset UpdatedAt,
    Uri? PreviewUrl);

public sealed record OwnerGameMedia(
    long GameId,
    string GameTitle,
    IReadOnlyList<OwnerGameMediaItem> Covers,
    IReadOnlyList<OwnerGameMediaItem> Screenshots);

public sealed record PublicGameMediaImage(Guid MediaId, Uri Url);

public sealed record PublicGameMedia(
    PublicGameMediaImage? Cover,
    IReadOnlyList<PublicGameMediaImage> Screenshots);

public sealed class GameMediaQueryService
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

    public GameMediaQueryService(
        AppDbContext db,
        IMediaObjectStorage storage)
    {
        _db = db;
        _storage = storage;
    }

    public async Task<OwnerGameMedia?> GetOwnerAsync(
        long ownerUserId,
        long gameId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ownerUserId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gameId);

        var game = await _db.Games
            .AsNoTracking()
            .Where(item => item.Id == gameId && item.OwnerId == ownerUserId)
            .Select(item => new OwnerGameSnapshot(item.Id, item.Title))
            .SingleOrDefaultAsync(cancellationToken);
        if (game is null)
        {
            return null;
        }

        var snapshots = await _db.MediaAssets
            .AsNoTracking()
            .Where(media => media.GameId == gameId
                && OwnerVisibleStatuses.Contains(media.Status))
            .Select(media => new MediaSnapshot(
                media.Id,
                media.Revision,
                media.AttachmentKind,
                media.Status,
                media.IsCurrent,
                media.SortOrder,
                media.DeclaredBytes,
                media.ProcessingAttemptCount,
                media.FailureCode,
                media.UploadExpiresAt,
                media.PublishedAt,
                media.CreatedAt,
                media.UpdatedAt))
            .ToArrayAsync(cancellationToken);
        var items = new List<OwnerGameMediaItem>(snapshots.Length);
        foreach (var snapshot in snapshots
                     .OrderBy(item => item.AttachmentKind)
                     .ThenBy(item => item.Status == MediaStatus.Ready ? 0 : 1)
                     .ThenBy(item => item.SortOrder ?? int.MaxValue)
                     .ThenBy(item => item.CreatedAt)
                     .ThenBy(item => item.MediaId))
        {
            var previewUrl = snapshot.Status == MediaStatus.Ready
                ? await CreateDownloadUrlAsync(
                    snapshot.MediaId,
                    snapshot.Revision,
                    MediaDerivedVariant.GameList,
                    cancellationToken)
                : null;
            items.Add(new OwnerGameMediaItem(
                snapshot.MediaId,
                snapshot.AttachmentKind,
                snapshot.Status,
                snapshot.IsCurrent,
                snapshot.SortOrder,
                snapshot.DeclaredBytes,
                snapshot.ProcessingAttemptCount,
                snapshot.FailureCode,
                snapshot.UploadExpiresAt,
                snapshot.PublishedAt,
                snapshot.UpdatedAt,
                previewUrl));
        }

        return new OwnerGameMedia(
            game.GameId,
            game.Title,
            items.Where(item =>
                    item.AttachmentKind == MediaAttachmentKind.GameCover)
                .ToArray(),
            items.Where(item =>
                    item.AttachmentKind == MediaAttachmentKind.GameScreenshot)
                .ToArray());
    }

    public async Task<PublicGameMedia?> GetPublicDetailAsync(
        long gameId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gameId);

        if (!await _db.Games.AsNoTracking().AnyAsync(
                game => game.Id == gameId,
                cancellationToken))
        {
            return null;
        }

        return await GetPublicDetailForExistingGameAsync(
            gameId,
            cancellationToken);
    }

    public async Task<PublicGameMedia> GetPublicDetailForExistingGameAsync(
        long gameId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gameId);

        var media = await _db.MediaAssets
            .AsNoTracking()
            .Where(item => item.GameId == gameId
                && item.Status == MediaStatus.Ready
                && item.IsCurrent)
            .OrderBy(item => item.AttachmentKind)
            .ThenBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .Select(item => new PublicSnapshot(
                item.Id,
                item.Revision,
                item.AttachmentKind))
            .ToArrayAsync(cancellationToken);
        PublicGameMediaImage? cover = null;
        var screenshots = new List<PublicGameMediaImage>();
        foreach (var item in media)
        {
            var image = new PublicGameMediaImage(
                item.MediaId,
                await CreateDownloadUrlAsync(
                    item.MediaId,
                    item.Revision,
                    MediaDerivedVariant.GameDisplay,
                    cancellationToken));
            if (item.AttachmentKind == MediaAttachmentKind.GameCover)
            {
                cover = image;
            }
            else if (item.AttachmentKind == MediaAttachmentKind.GameScreenshot)
            {
                screenshots.Add(image);
            }
        }

        return new PublicGameMedia(cover, screenshots);
    }

    public async Task<IReadOnlyDictionary<long, PublicGameMediaImage>>
        GetPublicCoversAsync(
            IReadOnlyCollection<long> gameIds,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gameIds);
        if (gameIds.Any(gameId => gameId <= 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(gameIds),
                "Game identifiers must be positive.");
        }

        var distinctGameIds = gameIds.Distinct().ToArray();
        if (distinctGameIds.Length == 0)
        {
            return new Dictionary<long, PublicGameMediaImage>();
        }

        var covers = await _db.MediaAssets
            .AsNoTracking()
            .Where(media => media.GameId != null
                && distinctGameIds.Contains(media.GameId.Value)
                && media.AttachmentKind == MediaAttachmentKind.GameCover
                && media.Status == MediaStatus.Ready
                && media.IsCurrent)
            .OrderBy(media => media.GameId)
            .Select(media => new PublicCoverSnapshot(
                media.GameId!.Value,
                media.Id,
                media.Revision))
            .ToArrayAsync(cancellationToken);
        var result = new Dictionary<long, PublicGameMediaImage>(covers.Length);
        foreach (var cover in covers)
        {
            result.Add(
                cover.GameId,
                new PublicGameMediaImage(
                    cover.MediaId,
                    await CreateDownloadUrlAsync(
                        cover.MediaId,
                        cover.Revision,
                        MediaDerivedVariant.GameList,
                        cancellationToken)));
        }

        return result;
    }

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
        MediaAttachmentKind AttachmentKind,
        MediaStatus Status,
        bool IsCurrent,
        int? SortOrder,
        long DeclaredBytes,
        int ProcessingAttemptCount,
        string? FailureCode,
        DateTimeOffset UploadExpiresAt,
        DateTimeOffset? PublishedAt,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    private sealed record OwnerGameSnapshot(long GameId, string Title);

    private sealed record PublicSnapshot(
        Guid MediaId,
        int Revision,
        MediaAttachmentKind AttachmentKind);

    private sealed record PublicCoverSnapshot(
        long GameId,
        Guid MediaId,
        int Revision);
}
