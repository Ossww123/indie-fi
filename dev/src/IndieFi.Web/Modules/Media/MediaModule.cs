using Amazon.S3;
using IndieFi.Web.Modules.KeyDistributions.Infrastructure;
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
        services.AddOptions<MediaUploadRateLimitOptions>()
            .Bind(configuration.GetSection(MediaUploadRateLimitOptions.SectionName))
            .Validate(
                MediaUploadRateLimitOptions.IsValid,
                "Media upload rate-limit settings are invalid.")
            .ValidateOnStart();
        services.TryAddSingleton<WaitingRoomRedisConnection>();
        services.AddSingleton<MediaUploadRateLimitKeys>();
        services.AddSingleton<
            IMediaUploadIssueRateLimiter,
            RedisMediaUploadIssueRateLimiter>();
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
        services.AddScoped<
            IMediaProcessingPublisher,
            MassTransitMediaProcessingPublisher>();
        services.AddScoped<
            IMediaDeletionPublisher,
            MassTransitMediaDeletionPublisher>();
        services.AddScoped<MediaUploadUrlService>();
        services.AddScoped<MediaUploadCompletionService>();
        services.AddScoped<MediaPublicationService>();
        services.AddHealthChecks()
            .AddCheck<MediaObjectStorageHealthCheck>("media-object-storage");
        return services;
    }
}
