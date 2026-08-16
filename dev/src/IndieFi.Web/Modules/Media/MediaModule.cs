using Amazon.S3;
using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Infrastructure;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace IndieFi.Web.Modules.Media;

public static class MediaModule
{
    public static IServiceCollection AddMediaModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddScoped<MediaUploadReservationService>();
        services.AddOptions<MediaObjectStorageOptions>()
            .Bind(configuration.GetSection(MediaObjectStorageOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<
            IValidateOptions<MediaObjectStorageOptions>,
            MediaObjectStorageOptionsValidator>();

        var objectStorageEnabled = configuration.GetValue<bool>(
            $"{MediaObjectStorageOptions.SectionName}:Enabled");
        if (!objectStorageEnabled)
        {
            return services;
        }

        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IAmazonS3>(provider =>
            S3ClientFactory.Create(provider
                .GetRequiredService<IOptions<MediaObjectStorageOptions>>()
                .Value));
        services.AddSingleton<S3MediaObjectStorage>();
        services.AddSingleton<IMediaObjectStorage>(provider =>
            provider.GetRequiredService<S3MediaObjectStorage>());
        services.AddHealthChecks()
            .AddCheck<MediaObjectStorageHealthCheck>("media-object-storage");
        return services;
    }
}
