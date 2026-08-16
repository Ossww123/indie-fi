using IndieFi.Web.Modules.KeyDistributions.Infrastructure;
using StackExchange.Redis;

namespace IndieFi.Web.Infrastructure.Realtime;

public static class RealtimeModule
{
    public static IServiceCollection AddIndieFiRealtime(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<WaitingRoomRedisOptions>()
            .Bind(configuration.GetSection(WaitingRoomRedisOptions.SectionName))
            .Validate(
                WaitingRoomRedisOptions.IsValid,
                "Shared Redis settings are invalid.")
            .ValidateOnStart();

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
            backplane.ConnectTimeout = checked(
                (int)redis.ConnectTimeout.TotalMilliseconds);
            backplane.ChannelPrefix = RedisChannel.Literal(
                $"{redis.KeyPrefix}:signalr");
            signalR.AddStackExchangeRedis(options =>
            {
                options.Configuration = backplane;
            });
        }

        return services;
    }
}
