using IndieFi.Web.Modules.Games.Application;

namespace IndieFi.Web.Modules.Games;

public static class GamesModule
{
    public static IServiceCollection AddGamesModule(this IServiceCollection services)
    {
        services.AddScoped<GameManagementService>();
        services.AddScoped<GamePlayLinkManagementService>();
        services.AddScoped<GameClassificationCatalogQueryService>();
        services.AddScoped<GameClassificationSelectionService>();
        return services;
    }
}
