using IndieFi.Web.Modules.KeyDistributions.Application;

namespace IndieFi.Web.Modules.KeyDistributions;

public static class KeyDistributionsModule
{
    public static IServiceCollection AddKeyDistributionsModule(
        this IServiceCollection services)
    {
        services.AddScoped<KeyClaimService>();
        services.AddScoped<KeyDropInventoryService>();
        services.AddScoped<KeyDropSchedulingService>();
        services.AddScoped<GameKeyRegistrationWriter>();
        services.AddScoped<GrantedGameKeyReader>();
        services.AddScoped<PublicKeyDropReader>();
        services.AddSingleton<GameKeyProtectionService>();
        return services;
    }
}
