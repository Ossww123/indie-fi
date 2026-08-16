using IndieFi.Web.Data;
using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Domain;
using IndieFi.Web.Modules.Notifications.Infrastructure;
using MassTransit;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

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
        services.AddScoped<
            IKeyDropNotificationFanoutPublisher,
            MassTransitKeyDropNotificationFanoutPublisher>();
        services.AddScoped<
            ISocialNotificationFanoutPublisher,
            MassTransitSocialNotificationFanoutPublisher>();
        services.AddSingleton<NotificationFactory>();
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddScoped<NotificationProjectionService>();
        services.AddScoped<NotificationInboxService>();
        services.AddScoped<NotificationPreferenceService>();
        services.AddScoped<KeyDropSubscriptionService>();
        services.AddScoped<FavoriteGameKeyDropFanoutService>();
        services.AddScoped<SubscribedKeyDropOpenedFanoutService>();
        services.AddScoped<FollowedUserGamePublishedFanoutService>();
        services.AddSignalR();
        services.AddSingleton<
            INotificationRealtimeNotifier,
            SignalRNotificationRealtimeNotifier>();
        return services;
    }

    public static IServiceCollection AddNotificationEmailDelivery(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<NotificationEmailDeliveryOptions>()
            .Bind(configuration.GetSection(NotificationEmailDeliveryOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<
            IValidateOptions<NotificationEmailDeliveryOptions>,
            NotificationEmailDeliveryOptionsValidator>();
        services.AddSingleton<NotificationEmailTemplateRenderer>();
        services.AddSingleton<NotificationEmailMessageFactory>();
        services.AddSingleton<INotificationEmailSender, SmtpNotificationEmailSender>();
        services.AddScoped<NotificationEmailDeliveryAuthorityService>();
        services.AddScoped<NotificationEmailDeliveryProcessor>();
        services.AddHostedService(provider =>
        {
            var options = provider
                .GetRequiredService<IOptions<NotificationEmailDeliveryOptions>>()
                .Value;
            return new NotificationEmailDeliveryWorker(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<ILogger<NotificationEmailDeliveryWorker>>(),
                options.Enabled,
                options.WorkerInterval,
                options.BatchSize);
        });
        return services;
    }
}
