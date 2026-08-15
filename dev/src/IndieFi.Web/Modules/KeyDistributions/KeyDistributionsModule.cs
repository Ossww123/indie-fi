using IndieFi.Web.Modules.KeyDistributions.Application;

namespace IndieFi.Web.Modules.KeyDistributions;

public static class KeyDistributionsModule
{
    public static IServiceCollection AddKeyDistributionsModule(
        this IServiceCollection services)
    {
        services.AddScoped<KeyClaimService>();
        services.AddScoped<KeyDropSchedulingService>();
        services.AddSingleton<GameKeyProtectionService>();
        return services;
    }
}
