using MassTransit;

namespace IndieFi.Web.Modules.Media.Infrastructure;

public static class MediaConsumerRegistration
{
    public static void AddMediaConsumers(
        this IBusRegistrationConfigurator registration,
        IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>(
                $"{MediaWorkerOptions.SectionName}:Enabled"))
        {
            return;
        }

        registration.AddConsumer<MediaProcessingRequestedConsumer>();
        registration.AddConsumer<MediaDeletionRequestedConsumer>();
    }
}
