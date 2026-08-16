using MassTransit;

namespace IndieFi.Web.Modules.Notifications.Infrastructure;

public static class NotificationConsumerRegistration
{
    public static void AddNotificationConsumers(
        this IBusRegistrationConfigurator registration)
    {
        registration.AddConsumer<UserNotificationRequestedConsumer>();
    }
}
