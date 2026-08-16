using IndieFi.Web.Data;
using IndieFi.Web.Modules.Media.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Media.Application;

public sealed record MediaOwnerStatus(
    Guid MediaId,
    MediaAttachmentKind AttachmentKind,
    MediaStatus Status,
    bool IsCurrent,
    int? SortOrder,
    int ProcessingAttemptCount,
    string? FailureCode,
    DateTimeOffset UploadExpiresAt,
    DateTimeOffset? PublishedAt,
    DateTimeOffset UpdatedAt);

public sealed class MediaStatusQueryService
{
    private readonly AppDbContext _db;

    public MediaStatusQueryService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<MediaOwnerStatus?> FindAsync(
        long ownerUserId,
        Guid mediaId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ownerUserId);
        if (mediaId == Guid.Empty)
        {
            throw new ArgumentException(
                "A media identifier must not be empty.",
                nameof(mediaId));
        }

        return await _db.MediaAssets
            .AsNoTracking()
            .Where(item => item.Id == mediaId
                && item.OwnerUserId == ownerUserId)
            .Select(item => new MediaOwnerStatus(
                item.Id,
                item.AttachmentKind,
                item.Status,
                item.IsCurrent,
                item.SortOrder,
                item.ProcessingAttemptCount,
                item.FailureCode,
                item.UploadExpiresAt,
                item.PublishedAt,
                item.UpdatedAt))
            .SingleOrDefaultAsync(cancellationToken);
    }
}
