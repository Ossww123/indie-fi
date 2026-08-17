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
        var objectStorageEnabled = configuration.GetValue<bool>(
            $"{MediaObjectStorageOptions.SectionName}:Enabled");
        services.AddOptions<MediaWorkerOptions>()
            .Bind(configuration.GetSection(MediaWorkerOptions.SectionName))
            .Validate(
                MediaWorkerOptions.IsValid,
                "Media worker settings are invalid.")
            .Validate(
                options => !options.Enabled || objectStorageEnabled,
                "Media object storage must be enabled for the media Worker.")
            .ValidateOnStart();
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton<IMediaImageProcessor, SkiaMediaImageProcessor>();
        services.TryAddSingleton<
            IMediaImageDerivativeEncoder,
            SkiaMediaImageDerivativeEncoder>();
        var workerEnabled = configuration.GetValue<bool>(
            $"{MediaWorkerOptions.SectionName}:Enabled");
        if (workerEnabled)
        {
            services.AddScoped<MediaMessageAuthorityService>();
            services.AddScoped<MediaProcessingLeaseService>();
            services.AddScoped<MediaProcessingMessageDispatcher>();
            services.AddScoped<MediaDeletionMessageDispatcher>();
            services.AddScoped<
                IMediaProcessingWorkExecutor,
                MediaProcessingWorkExecutor>();
            services.AddScoped<MediaProcessingRecoveryService>();
            services.AddHostedService<MediaProcessingRecoveryWorker>();
        }

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

        if (!objectStorageEnabled)
        {
            return services;
        }

        services.AddSingleton<IAmazonS3>(provider =>
            S3ClientFactory.Create(provider
                .GetRequiredService<IOptions<MediaObjectStorageOptions>>()
                .Value));
        services.AddSingleton<S3MediaObjectStorage>();
        services.AddSingleton<IMediaObjectStorage>(provider =>
            provider.GetRequiredService<S3MediaObjectStorage>());
        services.AddSingleton<MediaObjectDeletionService>();
        services.AddScoped<
            IMediaProcessingPublisher,
            MassTransitMediaProcessingPublisher>();
        services.AddScoped<
            IMediaDeletionPublisher,
            MassTransitMediaDeletionPublisher>();
        services.AddScoped<MediaUploadUrlService>();
        services.AddScoped<MediaUploadCompletionService>();
        services.AddScoped<MediaPublicationService>();
        services.AddScoped<GameMediaQueryService>();
        services.AddScoped<ProfileMediaQueryService>();
        services.AddScoped<GameScreenshotOrderService>();
        if (workerEnabled)
        {
            services.AddScoped<
                IMediaProcessingAttemptExecutor,
                MediaProcessingAttemptExecutor>();
            services.AddScoped<MediaDeletionWorkExecutor>();
            services.AddScoped<IMediaDeletionWorkExecutor>(provider =>
                provider.GetRequiredService<MediaDeletionWorkExecutor>());
            services.AddScoped<MediaCleanupService>();
            services.AddScoped<MediaOperationalSnapshotService>();
            services.AddHostedService<MediaCleanupWorker>();
            services.AddHostedService<MediaOperationsWorker>();
        }

        services.AddScoped<MediaStatusQueryService>();
        services.AddHealthChecks()
            .AddCheck<MediaObjectStorageHealthCheck>("media-object-storage");
        return services;
    }
}
