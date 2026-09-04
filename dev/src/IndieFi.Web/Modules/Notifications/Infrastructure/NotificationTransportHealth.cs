using MassTransit;

namespace IndieFi.Web.Modules.Notifications.Infrastructure;

public interface INotificationTransportHealth
{
    bool IsReady();
}

public sealed class MassTransitNotificationTransportHealth :
    INotificationTransportHealth
{
    private readonly IBusControl _bus;

    public MassTransitNotificationTransportHealth(IBusControl bus)
    {
        _bus = bus;
    }

    public bool IsReady() => string.Equals(
        _bus.CheckHealth().Status.ToString(),
        "Healthy",
        StringComparison.Ordinal);
}
