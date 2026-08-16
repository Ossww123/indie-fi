using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Contracts;
using MassTransit;

namespace IndieFi.Web.Modules.Notifications.Infrastructure;

public sealed class UserNotificationRequestedConsumer :
    IConsumer<UserNotificationRequestedV1>
{
    private readonly NotificationProjectionService _projection;
    private readonly INotificationRealtimeNotifier _realtime;
    private readonly ILogger<UserNotificationRequestedConsumer> _logger;

    public UserNotificationRequestedConsumer(
        NotificationProjectionService projection,
        INotificationRealtimeNotifier realtime,
        ILogger<UserNotificationRequestedConsumer> logger)
    {
        _projection = projection;
        _realtime = realtime;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<UserNotificationRequestedV1> context)
    {
        var result = await _projection.ProjectAsync(
            context.Message,
            context.CancellationToken);

        if (result.Outcome == NotificationProjectionOutcome.IgnoredInvalidContract)
        {
            _logger.LogWarning(
                "Notification projection ignored an invalid {NotificationKind} contract.",
                context.Message.Kind);
            return;
        }

        if (result.Outcome == NotificationProjectionOutcome.Created
            && result.IsWebVisible)
        {
            await _realtime.NotifyChangedAsync(
                context.Message.RecipientUserId,
                context.CancellationToken);
        }

        _logger.LogInformation(
            "Notification projection kind={NotificationKind} outcome={ProjectionOutcome}",
            context.Message.Kind,
            result.Outcome);
    }
}
