using IndieFi.Web.Infrastructure.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class ObservabilityRegistrationTests
{
    [Fact]
    public void Disabled_AllowsCollectorFreeHost()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Observability:Enabled"] = "false",
            ["Observability:OtlpEndpoint"] = string.Empty,
        });

        builder.Services.AddIndieFiObservability(
            builder.Logging,
            builder.Configuration,
            builder.Environment,
            ObservabilityServiceNames.Worker);

        using var host = builder.Build();
        var options = host.Services
            .GetRequiredService<IOptions<ObservabilityOptions>>()
            .Value;

        Assert.False(options.Enabled);
        Assert.Null(host.Services.GetService<TracerProvider>());
        Assert.Null(host.Services.GetService<MeterProvider>());
    }

    [Fact]
    public void Enabled_RegistersTraceAndMetricProvidersWithoutCollectorConnection()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(EnabledSettings());

        builder.Services.AddIndieFiObservability(
            builder.Logging,
            builder.Configuration,
            builder.Environment,
            ObservabilityServiceNames.Worker);

        using var host = builder.Build();

        Assert.NotNull(host.Services.GetRequiredService<TracerProvider>());
        Assert.NotNull(host.Services.GetRequiredService<MeterProvider>());
    }

    [Theory]
    [InlineData("ftp://127.0.0.1:4317")]
    [InlineData("http://user:secret@127.0.0.1:4317")]
    [InlineData("http://127.0.0.1:4317?token=secret")]
    [InlineData("not-a-uri")]
    public void Enabled_RejectsUnsafeEndpoint(string endpoint)
    {
        var builder = Host.CreateApplicationBuilder();
        var settings = EnabledSettings();
        settings["Observability:OtlpEndpoint"] = endpoint;
        builder.Configuration.AddInMemoryCollection(settings);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.Services.AddIndieFiObservability(
                builder.Logging,
                builder.Configuration,
                builder.Environment,
                ObservabilityServiceNames.Worker));

        Assert.Contains("Observability", exception.Message);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void Enabled_RejectsInvalidSamplingRatio(double ratio)
    {
        var options = new ObservabilityOptions
        {
            Enabled = true,
            TraceSamplingRatio = ratio,
        };

        Assert.False(ObservabilityOptions.IsValid(options));
    }

    private static Dictionary<string, string?> EnabledSettings() => new()
    {
        ["Observability:Enabled"] = "true",
        ["Observability:OtlpEndpoint"] = "http://127.0.0.1:4317",
        ["Observability:TraceSamplingRatio"] = "0.1",
        ["Observability:ServiceVersion"] = "test-commit",
        ["Observability:InstanceId"] = "test-worker-1",
    };
}
