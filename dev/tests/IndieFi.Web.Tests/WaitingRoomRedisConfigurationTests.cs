using IndieFi.Web.Modules.KeyDistributions.Infrastructure;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class WaitingRoomRedisConfigurationTests
{
    [Fact]
    public void IsValid_WhenDisabled_AllowsEmptyConnectionString()
    {
        var options = new WaitingRoomRedisOptions
        {
            Enabled = false,
            ConnectionString = string.Empty,
        };

        Assert.True(WaitingRoomRedisOptions.IsValid(options));
    }

    [Theory]
    [InlineData("", "indiefi:waiting-room:v1", 2)]
    [InlineData("localhost:6379", "contains space", 2)]
    [InlineData("localhost:6379", "indiefi:waiting-room:v1", 0)]
    [InlineData("localhost:6379", "indiefi:waiting-room:v1", 31)]
    public void IsValid_WhenEnabled_RejectsIncompleteOrUnsafeSettings(
        string connectionString,
        string keyPrefix,
        int connectTimeoutSeconds)
    {
        var options = new WaitingRoomRedisOptions
        {
            Enabled = true,
            ConnectionString = connectionString,
            KeyPrefix = keyPrefix,
            ConnectTimeout = TimeSpan.FromSeconds(connectTimeoutSeconds),
        };

        Assert.False(WaitingRoomRedisOptions.IsValid(options));
    }

    [Fact]
    public async Task Connection_WhenDisabled_FailsClosedWithoutConnecting()
    {
        await using var connection = NewConnection(new WaitingRoomRedisOptions
        {
            Enabled = false,
        });

        await Assert.ThrowsAsync<WaitingRoomUnavailableException>(() =>
            connection.GetAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HealthCheck_WhenFeatureIsDisabled_IsHealthyWithoutRedis()
    {
        await using var connection = NewConnection(new WaitingRoomRedisOptions
        {
            Enabled = false,
        });
        var healthCheck = new WaitingRoomRedisHealthCheck(connection);

        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    private static WaitingRoomRedisConnection NewConnection(
        WaitingRoomRedisOptions options) =>
        new(Options.Create(options));
}
