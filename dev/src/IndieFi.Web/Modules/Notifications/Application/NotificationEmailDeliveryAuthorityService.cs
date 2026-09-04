using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Accounts.Domain;
using IndieFi.Web.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Notifications.Application;

public enum NotificationEmailPreparationOutcome
{
    Ready,
    Expired,
}

public sealed record NotificationEmailPreparation(
    NotificationEmailPreparationOutcome Outcome,
    NotificationEmailSendRequest? Request = null);

public sealed class NotificationEmailDeliveryAuthorityService
{
    private readonly AppDbContext _db;
    private readonly EmailProtectionService _emailProtection;
    private readonly NotificationEmailTemplateRenderer _renderer;

    public NotificationEmailDeliveryAuthorityService(
        AppDbContext db,
        EmailProtectionService emailProtection,
        NotificationEmailTemplateRenderer renderer)
    {
        _db = db;
        _emailProtection = emailProtection;
        _renderer = renderer;
    }

    public async Task<NotificationDelivery?> ClaimNextDueAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (_db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "A transaction is required while claiming an email delivery.");
        }

        var candidates = await _db.NotificationDeliveries
            .FromSqlInterpolated(
                $"""
                SELECT *
                  FROM notification_deliveries
                 WHERE channel = 'Email'
                   AND status = 'Pending'
                   AND next_attempt_at <= {now}
                 ORDER BY next_attempt_at, id
                 LIMIT 1
                   FOR UPDATE SKIP LOCKED
                """)
            .Include(delivery => delivery.Notification)
            .ToListAsync(cancellationToken);
        return candidates.SingleOrDefault();
    }

    public async Task<NotificationEmailPreparation> PrepareAsync(
        NotificationDelivery delivery,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (delivery.Channel != NotificationChannel.Email
            || delivery.Status != NotificationDeliveryStatus.Pending)
        {
            throw new InvalidOperationException(
                "Only pending email deliveries can be prepared.");
        }

        var notification = delivery.Notification;
        if (!await IsStillValidAsync(notification, now, cancellationToken))
        {
            return new(NotificationEmailPreparationOutcome.Expired);
        }

        var recipient = await _db.Users
            .AsNoTracking()
            .Where(user => user.Id == notification.UserId)
            .Select(user => new ProtectedEmailAddress(
                user.EmailCiphertext,
                user.EmailNonce,
                user.EmailAuthenticationTag,
                user.EmailEncryptionKeyId))
            .SingleAsync(cancellationToken);
        var recipientAddress = _emailProtection.Unprotect(recipient);
        return new(
            NotificationEmailPreparationOutcome.Ready,
            _renderer.Render(delivery, recipientAddress));
    }

    private async Task<bool> IsStillValidAsync(
        Notification notification,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (notification.ExpiresAt <= now
            || notification.ContentRemovedAt is not null
            || notification.Title is null
            || notification.Body is null)
        {
            return false;
        }

        return notification.Kind switch
        {
            NotificationKind.ReviewAssigned or
            NotificationKind.ReviewDeadlineSoon =>
                await _db.ReviewAssignments.AsNoTracking().AnyAsync(
                    assignment => assignment.Id == notification.SourceId
                        && assignment.ReviewerId == notification.UserId
                        && assignment.Status == ReviewAssignmentStatus.Active
                        && assignment.LeasedUntil > now,
                    cancellationToken),
            NotificationKind.ReviewExpired =>
                await _db.ReviewAssignments.AsNoTracking().AnyAsync(
                    assignment => assignment.Id == notification.SourceId
                        && assignment.ReviewerId == notification.UserId
                        && assignment.Status == ReviewAssignmentStatus.Expired,
                    cancellationToken),
            NotificationKind.FeedbackCompleted =>
                await (
                    from feedback in _db.FeedbackSubmissions.AsNoTracking()
                    join assignment in _db.ReviewAssignments.AsNoTracking()
                        on feedback.AssignmentId equals assignment.Id
                    join request in _db.ReviewRequests.AsNoTracking()
                        on assignment.RequestId equals request.Id
                    where feedback.Id == notification.SourceId
                        && request.RequesterId == notification.UserId
                    select feedback.Id)
                .AnyAsync(cancellationToken),
            NotificationKind.KeyDropOpened =>
                await (
                    from drop in _db.KeyDrops.AsNoTracking()
                    join subscription in _db.KeyDropSubscriptions.AsNoTracking()
                        on drop.Id equals subscription.DropId
                    where drop.Id == notification.SourceId
                        && subscription.UserId == notification.UserId
                        && drop.OpensAt <= now
                        && drop.ClosedAt == null
                    select drop.Id)
                .AnyAsync(cancellationToken),
            NotificationKind.FavoriteGameKeyDropCreated =>
                await (
                    from drop in _db.KeyDrops.AsNoTracking()
                    join favorite in _db.FavoriteGames.AsNoTracking()
                        on drop.GameId equals favorite.GameId
                    where drop.Id == notification.SourceId
                        && favorite.UserId == notification.UserId
                        && drop.ClosedAt == null
                    select drop.Id)
                .AnyAsync(cancellationToken),
            NotificationKind.FollowedUserGamePublished =>
                await (
                    from game in _db.Games.AsNoTracking()
                    join follow in _db.UserFollows.AsNoTracking()
                        on game.OwnerId equals follow.FollowedUserId
                    where game.Id == notification.SourceId
                        && follow.FollowerId == notification.UserId
                        && game.OwnerId != notification.UserId
                    select game.Id)
                .AnyAsync(cancellationToken),
            NotificationKind.AccountStatusChanged =>
                notification.SourceId == notification.UserId
                && await _db.Users.AsNoTracking().AnyAsync(
                    user => user.Id == notification.UserId,
                    cancellationToken),
            NotificationKind.ReportResolved => false,
            _ => false,
        };
    }
}
