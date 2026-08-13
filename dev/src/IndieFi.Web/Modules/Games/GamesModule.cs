using IndieFi.Web.Services;

namespace IndieFi.Web.Modules.Games;

public static class GamesModule
{
    public static IServiceCollection AddGamesModule(this IServiceCollection services)
    {
        services.AddScoped<GameManagementService>();
        return services;
    }
}
