using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace IndieFi.Web.Modules.KeyDistributions.Infrastructure;

public sealed class WaitingRoomRedisHealthCheck : IHealthCheck
{
    private readonly WaitingRoomRedisConnection _connection;

    public WaitingRoomRedisHealthCheck(WaitingRoomRedisConnection connection)
    {
        _connection = connection;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_connection.Enabled)
        {
            return HealthCheckResult.Healthy("Virtual waiting room is disabled.");
        }

        try
        {
            var multiplexer = await _connection.GetAsync(cancellationToken);
            var latency = await multiplexer.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy(
                "Virtual waiting-room Redis is reachable.",
                new Dictionary<string, object>
                {
                    ["latency_ms"] = latency.TotalMilliseconds,
                });
        }
        catch (Exception exception)
            when (exception is WaitingRoomUnavailableException
                or RedisConnectionException
                or RedisTimeoutException)
        {
            return HealthCheckResult.Unhealthy(
                "Virtual waiting-room Redis is unavailable.",
                exception);
        }
    }
}
