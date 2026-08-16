using IndieFi.Web.Data;
using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Domain;
using IndieFi.Web.Modules.Notifications.Infrastructure;
using MassTransit;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IndieFi.Web.Modules.Notifications;

public static class NotificationsModule
{
    public static IServiceCollection AddNotificationsModule(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<IBusRegistrationConfigurator>? configureConsumers = null)
    {
        services.AddOptions<RabbitMqOptions>()
            .Bind(configuration.GetSection(RabbitMqOptions.SectionName))
            .Validate(RabbitMqOptions.IsValid, "RabbitMQ settings are invalid.")
            .ValidateOnStart();

        var rabbitMq = configuration
            .GetSection(RabbitMqOptions.SectionName)
            .Get<RabbitMqOptions>() ?? new RabbitMqOptions();

        services.AddMassTransit(registration =>
        {
            configureConsumers?.Invoke(registration);
            registration.SetEndpointNameFormatter(
                new KebabCaseEndpointNameFormatter(
                    rabbitMq.EndpointPrefix,
                    includeNamespace: false));
            registration.AddEntityFrameworkOutbox<AppDbContext>(outbox =>
            {
                outbox.DuplicateDetectionWindow =
                    NotificationPolicy.ConsumerDeduplicationRetention;
                outbox.QueryDelay = TimeSpan.FromSeconds(1);
                outbox.UsePostgres();
                outbox.UseBusOutbox(busOutbox =>
                {
                    busOutbox.MessageDeliveryLimit = 100;
                    busOutbox.MessageDeliveryTimeout = TimeSpan.FromSeconds(10);
                });
            });
            registration.AddConfigureEndpointsCallback(
                (context, _, endpoint) =>
                {
                    endpoint.UseMessageRetry(retry => retry.Intervals(
                        NotificationPolicy.ConsumerRetryDelays.ToArray()));
                    endpoint.UseEntityFrameworkOutbox<AppDbContext>(context);
                });

            if (rabbitMq.Enabled)
            {
                registration.UsingRabbitMq((context, bus) =>
                {
                    bus.Host(
                        rabbitMq.Host,
                        rabbitMq.Port,
                        rabbitMq.VirtualHost,
                        host =>
                        {
                            host.Username(rabbitMq.Username);
                            host.Password(rabbitMq.Password);
                        });
                    bus.ConfigureEndpoints(context);
                });
            }
            else
            {
                registration.UsingInMemory((context, bus) =>
                    bus.ConfigureEndpoints(context));
            }
        });
        services.AddScoped<
            INotificationEventPublisher,
            MassTransitNotificationEventPublisher>();
        services.AddSingleton<NotificationFactory>();
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddScoped<NotificationProjectionService>();
        services.AddScoped<NotificationInboxService>();
        services.AddScoped<NotificationPreferenceService>();
        services.AddSignalR();
        services.AddSingleton<
            INotificationRealtimeNotifier,
            SignalRNotificationRealtimeNotifier>();
        return services;
    }
}
