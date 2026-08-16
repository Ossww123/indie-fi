namespace IndieFi.Web.Modules.Notifications.Application;

public interface ISocialNotificationFanoutPublisher
{
    Task StageGamePublishedAsync(
        Guid fanoutId,
        long gameId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);
}
