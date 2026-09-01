using IndieFi.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class DatabaseReadinessHealthCheckTests
{
    private readonly PostgreSqlFixture _fixture;

    public DatabaseReadinessHealthCheckTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CheckHealthAsync_WhenDatabaseIsReachable_ReturnsHealthy()
    {
        await using var provider = BuildProvider(_fixture.ConnectionString);
        var check = new DatabaseReadinessHealthCheck(
            provider.GetRequiredService<IServiceScopeFactory>());

        var result = await check.CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.True(result.Data.ContainsKey("latency_ms"));
    }

    [Fact]
    public async Task CheckHealthAsync_WhenDatabaseIsUnavailable_ReturnsUnhealthy()
    {
        var unavailable = new NpgsqlConnectionStringBuilder(
            _fixture.ConnectionString)
        {
            Host = "127.0.0.1",
            Port = 1,
            Timeout = 1,
            Pooling = false,
        };
        await using var provider = BuildProvider(unavailable.ConnectionString);
        var check = new DatabaseReadinessHealthCheck(
            provider.GetRequiredService<IServiceScopeFactory>());

        var result = await check.CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    private static ServiceProvider BuildProvider(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(connectionString));
        return services.BuildServiceProvider();
    }
}
