namespace IndieFi.Web.Modules.Notifications.Application;

public interface INotificationRealtimeNotifier
{
    Task NotifyChangedAsync(
        long userId,
        CancellationToken cancellationToken = default);
}
