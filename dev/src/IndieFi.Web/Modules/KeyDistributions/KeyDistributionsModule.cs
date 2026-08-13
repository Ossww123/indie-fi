using IndieFi.Web.Services;

namespace IndieFi.Web.Modules.KeyDistributions;

public static class KeyDistributionsModule
{
    public static IServiceCollection AddKeyDistributionsModule(
        this IServiceCollection services)
    {
        services.AddScoped<KeyClaimService>();
        services.AddScoped<KeyDropSchedulingService>();
        return services;
    }
}
