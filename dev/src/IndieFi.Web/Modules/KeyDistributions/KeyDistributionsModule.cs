using IndieFi.Web.Modules.KeyDistributions.Application;
using IndieFi.Web.Modules.KeyDistributions.Infrastructure;

namespace IndieFi.Web.Modules.KeyDistributions;

public static class KeyDistributionsModule
{
    public static IServiceCollection AddKeyDistributionsModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<WaitingRoomRedisOptions>()
            .Bind(configuration.GetSection(WaitingRoomRedisOptions.SectionName))
            .Validate(
                WaitingRoomRedisOptions.IsValid,
                "Waiting-room Redis settings are invalid.")
            .ValidateOnStart();
        services.AddOptions<WaitingRoomPolicyOptions>()
            .Bind(configuration.GetSection(WaitingRoomPolicyOptions.SectionName))
            .Validate(
                WaitingRoomPolicyOptions.IsValid,
                "Waiting-room policy settings are invalid.")
            .ValidateOnStart();
        services.AddSingleton<WaitingRoomRedisConnection>();
        services.AddSingleton<WaitingRoomRedisKeys>();
        services.AddSingleton<IWaitingRoomStore, RedisWaitingRoomStore>();
        services.AddHealthChecks()
            .AddCheck<WaitingRoomRedisHealthCheck>("waiting-room-redis");
        services.AddScoped<KeyClaimService>();
        services.AddScoped<KeyDropCloseService>();
        services.AddScoped<KeyDropEligibilityService>();
        services.AddScoped<KeyDropInventoryService>();
        services.AddScoped<KeyDropMutationLock>();
        services.AddScoped<KeyDropSchedulingService>();
        services.AddScoped<WaitingRoomParticipationService>();
        services.AddScoped<GameKeyRegistrationWriter>();
        services.AddScoped<GrantedGameKeyReader>();
        services.AddScoped<PublicKeyDropReader>();
        services.AddSingleton<GameKeyProtectionService>();
        return services;
    }
}
