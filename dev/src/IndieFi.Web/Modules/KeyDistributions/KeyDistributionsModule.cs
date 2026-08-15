using IndieFi.Web.Modules.KeyDistributions.Application;
using IndieFi.Web.Modules.KeyDistributions.Infrastructure;
using StackExchange.Redis;

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
        services.AddScoped<IKeyClaimAdmissionGate, WaitingRoomKeyClaimAdmissionGate>();
        services.AddSingleton<IWaitingRoomNotifier, SignalRWaitingRoomNotifier>();
        services.AddSingleton<IWaitingRoomLifecycle, WaitingRoomLifecycle>();
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
        var signalR = services.AddSignalR(options =>
        {
            options.EnableDetailedErrors = false;
            options.MaximumParallelInvocationsPerClient = 1;
            options.MaximumReceiveMessageSize = 16 * 1024;
        });
        var redis = configuration
            .GetSection(WaitingRoomRedisOptions.SectionName)
            .Get<WaitingRoomRedisOptions>() ?? new WaitingRoomRedisOptions();
        if (redis.Enabled)
        {
            var backplane = ConfigurationOptions.Parse(redis.ConnectionString);
            backplane.AbortOnConnectFail = false;
            backplane.ConnectTimeout = checked((int)redis.ConnectTimeout.TotalMilliseconds);
            backplane.ChannelPrefix = RedisChannel.Literal($"{redis.KeyPrefix}:signalr");
            signalR.AddStackExchangeRedis(options =>
            {
                options.Configuration = backplane;
            });
        }

        return services;
    }
}
