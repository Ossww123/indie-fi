using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Domain;
using IndieFi.Web.Modules.Notifications.Contracts;
using IndieFi.Web.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Notifications.Application;

public sealed class FollowedUserGamePublishedFanoutService
{
    public const int BatchSize = 250;

    private readonly AppDbContext _db;
    private readonly INotificationEventPublisher _notificationPublisher;

    public FollowedUserGamePublishedFanoutService(
        AppDbContext db,
        INotificationEventPublisher notificationPublisher)
    {
        _db = db;
        _notificationPublisher = notificationPublisher;
    }

    public async Task<NotificationFanoutBatchResult> StageBatchAsync(
        FollowedUserGamePublishedFanoutV1 message,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(message.FanoutId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(message.GameId);
        ArgumentOutOfRangeException.ThrowIfNegative(message.AfterUserId);

        var source = await _db.Games
            .AsNoTracking()
            .Where(game => game.Id == message.GameId)
            .Select(game => new { game.OwnerId })
            .SingleOrDefaultAsync(cancellationToken);
        if (source is null)
        {
            return new NotificationFanoutBatchResult(0);
        }

        var recipientIds = await (
                from follow in _db.UserFollows.AsNoTracking()
                join user in _db.Users.AsNoTracking()
                    on follow.FollowerId equals user.Id
                where follow.FollowedUserId == source.OwnerId
                where follow.FollowerId > message.AfterUserId
                where follow.FollowerId != source.OwnerId
                where user.AccountStatus == AccountStatus.Active
                    && user.SuspendedUntil == null
                orderby follow.FollowerId
                select follow.FollowerId)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var recipientId in recipientIds)
        {
            await _notificationPublisher.StageAsync(
                NotificationEventId.Create(
                    NotificationKind.FollowedUserGamePublished,
                    message.GameId,
                    recipientId),
                recipientId,
                NotificationKind.FollowedUserGamePublished,
                NotificationSourceTypes.Game,
                message.GameId,
                message.OccurredAt,
                cancellationToken);
        }

        return new NotificationFanoutBatchResult(
            recipientIds.Count,
            recipientIds.Count == BatchSize ? recipientIds[^1] : null);
    }
}
