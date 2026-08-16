using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Contracts;
using MassTransit;

namespace IndieFi.Web.Modules.Notifications.Infrastructure;

public sealed class UserNotificationRequestedConsumer :
    IConsumer<UserNotificationRequestedV1>
{
    private readonly NotificationProjectionService _projection;
    private readonly ILogger<UserNotificationRequestedConsumer> _logger;

    public UserNotificationRequestedConsumer(
        NotificationProjectionService projection,
        ILogger<UserNotificationRequestedConsumer> logger)
    {
        _projection = projection;
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

        _logger.LogInformation(
            "Notification projection kind={NotificationKind} outcome={ProjectionOutcome}",
            context.Message.Kind,
            result.Outcome);
    }
}
