using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Contracts;
using MassTransit;

namespace IndieFi.Web.Modules.Notifications.Infrastructure;

public sealed class UserNotificationsChangedConsumer :
    IConsumer<UserNotificationsChangedV1>
{
    private readonly INotificationRealtimeNotifier _realtime;

    public UserNotificationsChangedConsumer(
        INotificationRealtimeNotifier realtime)
    {
        _realtime = realtime;
    }

    public Task Consume(ConsumeContext<UserNotificationsChangedV1> context)
    {
        if (context.Message.RecipientUserId <= 0)
        {
            return Task.CompletedTask;
        }

        return _realtime.NotifyChangedAsync(
            context.Message.RecipientUserId,
            context.CancellationToken);
    }
}
