using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Domain;
using IndieFi.Web.Modules.Notifications.Contracts;
using IndieFi.Web.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Notifications.Application;

public sealed class SubscribedKeyDropOpenedFanoutService
{
    public const int BatchSize = 250;

    private readonly AppDbContext _db;
    private readonly INotificationEventPublisher _notificationPublisher;

    public SubscribedKeyDropOpenedFanoutService(
        AppDbContext db,
        INotificationEventPublisher notificationPublisher)
    {
        _db = db;
        _notificationPublisher = notificationPublisher;
    }

    public async Task<FavoriteGameKeyDropFanoutResult> StageBatchAsync(
        SubscribedKeyDropOpenedFanoutV1 message,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(message.FanoutId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(message.DropId);
        ArgumentOutOfRangeException.ThrowIfNegative(message.AfterUserId);

        var source = await _db.KeyDrops
            .AsNoTracking()
            .Where(item => item.Id == message.DropId)
            .Select(item => new { item.OwnerId })
            .SingleOrDefaultAsync(cancellationToken);
        if (source is null)
        {
            return new FavoriteGameKeyDropFanoutResult(0);
        }

        var recipientIds = await (
                from subscription in _db.KeyDropSubscriptions.AsNoTracking()
                join user in _db.Users.AsNoTracking()
                    on subscription.UserId equals user.Id
                where subscription.DropId == message.DropId
                where subscription.UserId > message.AfterUserId
                where subscription.UserId != source.OwnerId
                where user.AccountStatus == AccountStatus.Active
                    && user.SuspendedUntil == null
                orderby subscription.UserId
                select subscription.UserId)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var recipientId in recipientIds)
        {
            await _notificationPublisher.StageAsync(
                NotificationEventId.Create(
                    NotificationKind.KeyDropOpened,
                    message.DropId,
                    recipientId),
                recipientId,
                NotificationKind.KeyDropOpened,
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
