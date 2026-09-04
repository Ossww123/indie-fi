using System.Globalization;
using IndieFi.Web.Modules.Notifications.Application;
using Microsoft.AspNetCore.SignalR;

namespace IndieFi.Web.Modules.Notifications.Infrastructure;

public sealed class SignalRNotificationRealtimeNotifier :
    INotificationRealtimeNotifier
{
    public const string ChangedMethod = "NotificationsChanged";

    private readonly IHubContext<NotificationHub> _hubContext;
    private readonly ILogger<SignalRNotificationRealtimeNotifier> _logger;

    public SignalRNotificationRealtimeNotifier(
        IHubContext<NotificationHub> hubContext,
        ILogger<SignalRNotificationRealtimeNotifier> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task NotifyChangedAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        try
        {
            await _hubContext.Clients
                .User(userId.ToString(CultureInfo.InvariantCulture))
                .SendCoreAsync(ChangedMethod, [], cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Notification SignalR change signal failed.");
        }
    }
}
