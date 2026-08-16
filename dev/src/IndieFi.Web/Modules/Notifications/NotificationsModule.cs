using IndieFi.Web.Modules.Notifications.Infrastructure;

namespace IndieFi.Web.Modules.Notifications;

public static class NotificationsModule
{
    public static IServiceCollection AddNotificationsModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<RabbitMqOptions>()
            .Bind(configuration.GetSection(RabbitMqOptions.SectionName))
            .Validate(RabbitMqOptions.IsValid, "RabbitMQ settings are invalid.")
            .ValidateOnStart();
        return services;
    }
}
