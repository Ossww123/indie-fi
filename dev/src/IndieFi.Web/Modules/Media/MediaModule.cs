using IndieFi.Web.Modules.Media.Infrastructure;
using Microsoft.Extensions.Options;

namespace IndieFi.Web.Modules.Media;

public static class MediaModule
{
    public static IServiceCollection AddMediaModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<MediaObjectStorageOptions>()
            .Bind(configuration.GetSection(MediaObjectStorageOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<
            IValidateOptions<MediaObjectStorageOptions>,
            MediaObjectStorageOptionsValidator>();
        return services;
    }
}
