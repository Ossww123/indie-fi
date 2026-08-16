namespace IndieFi.Web.Modules.Notifications.Application;

public interface IKeyDropNotificationFanoutPublisher
{
    Task StageFavoriteGameCreatedAsync(
        Guid fanoutId,
        long dropId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);
}
