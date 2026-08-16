using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Domain;
using IndieFi.Web.Modules.Notifications.Contracts;
using IndieFi.Web.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Notifications.Application;

public sealed record FavoriteGameKeyDropFanoutResult(
    int StagedCount,
    long? NextAfterUserId = null);

public sealed class FavoriteGameKeyDropFanoutService
{
    public const int BatchSize = 250;

    private readonly AppDbContext _db;
    private readonly INotificationEventPublisher _notificationPublisher;

    public FavoriteGameKeyDropFanoutService(
        AppDbContext db,
        INotificationEventPublisher notificationPublisher)
    {
        _db = db;
        _notificationPublisher = notificationPublisher;
    }

    public async Task<FavoriteGameKeyDropFanoutResult> StageBatchAsync(
        FavoriteGameKeyDropCreatedFanoutV1 message,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(message.FanoutId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(message.DropId);
        ArgumentOutOfRangeException.ThrowIfNegative(message.AfterUserId);

        var source = await _db.KeyDrops
            .AsNoTracking()
            .Where(item => item.Id == message.DropId)
            .Select(item => new { item.GameId, item.OwnerId })
            .SingleOrDefaultAsync(cancellationToken);
        if (source is null)
        {
            return new FavoriteGameKeyDropFanoutResult(0);
        }

        var recipientIds = await (
                from favorite in _db.FavoriteGames.AsNoTracking()
                join user in _db.Users.AsNoTracking()
                    on favorite.UserId equals user.Id
                where favorite.GameId == source.GameId
                where favorite.UserId > message.AfterUserId
                where favorite.UserId != source.OwnerId
                where user.AccountStatus == AccountStatus.Active
                    && user.SuspendedUntil == null
                orderby favorite.UserId
                select favorite.UserId)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var recipientId in recipientIds)
        {
            await _notificationPublisher.StageAsync(
                NotificationEventId.Create(
                    NotificationKind.FavoriteGameKeyDropCreated,
                    message.DropId,
                    recipientId),
                recipientId,
                NotificationKind.FavoriteGameKeyDropCreated,
                NotificationSourceTypes.KeyDrop,
                message.DropId,
                message.OccurredAt,
                cancellationToken);
        }

        return new FavoriteGameKeyDropFanoutResult(
            recipientIds.Count,
            recipientIds.Count == BatchSize ? recipientIds[^1] : null);
    }
}
