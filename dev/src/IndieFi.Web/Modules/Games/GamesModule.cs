using IndieFi.Web.Modules.Games.Application;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IndieFi.Web.Modules.Games;

public static class GamesModule
{
    public static IServiceCollection AddGamesModule(this IServiceCollection services)
    {
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddMemoryCache();
        services.AddSingleton<PublicGameDiscoveryCursorCodec>();
        services.AddScoped<PublicGameDiscoveryQueryService>();
        services.AddScoped<GameManagementService>();
        services.AddScoped<GamePlayLinkManagementService>();
        services.AddScoped<GameClassificationCatalogQueryService>();
        services.AddScoped<GameClassificationSelectionService>();
        return services;
    }
}
