using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace IndieFi.Web.Data;

public sealed class DatabaseReadinessHealthCheck : IHealthCheck
{
    private readonly IServiceScopeFactory _scopeFactory;

    public DatabaseReadinessHealthCheck(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var database = scope.ServiceProvider
                .GetRequiredService<AppDbContext>();
            var canConnect = await database.Database
                .CanConnectAsync(cancellationToken);
            stopwatch.Stop();

            return canConnect
                ? HealthCheckResult.Healthy(
                    "PostgreSQL is reachable.",
                    new Dictionary<string, object>
                    {
                        ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds,
                    })
                : HealthCheckResult.Unhealthy(
                    "PostgreSQL is unavailable.");
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException)
        {
            stopwatch.Stop();
            return HealthCheckResult.Unhealthy(
                "PostgreSQL is unavailable.",
                exception,
                new Dictionary<string, object>
                {
                    ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds,
                });
        }
    }
}
