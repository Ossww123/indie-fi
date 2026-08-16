using IndieFi.Web.Modules.Notifications.Domain;

namespace IndieFi.Web.Modules.Notifications.Application;

public interface INotificationEventPublisher
{
    Task StageAsync(
        Guid eventId,
        long recipientUserId,
        NotificationKind kind,
        string sourceType,
        long sourceId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);
}
