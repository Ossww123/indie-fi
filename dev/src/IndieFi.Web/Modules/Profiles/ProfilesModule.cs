using IndieFi.Web.Modules.Profiles.Application;

namespace IndieFi.Web.Modules.Profiles;

public static class ProfilesModule
{
    public static IServiceCollection AddProfilesModule(this IServiceCollection services)
    {
        services.AddScoped<UserProfileManagementService>();
        return services;
    }
}
