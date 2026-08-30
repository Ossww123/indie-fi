using System.Diagnostics;
using IndieFi.Web.Infrastructure.Observability;
using Microsoft.AspNetCore.Http;
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

    [Fact]
    public void InboundHttp_ExcludesHealthProbeTraces()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/health/ready";

        Assert.False(ObservabilityPrivacy.ShouldTraceInboundHttp(context));
    }

    [Fact]
    public void OutboundHttp_RemovesSensitiveUrlTags()
    {
        using var activity = new Activity("test").Start();
        activity.SetTag(
            "url.full",
            "https://example.test/object?signature=secret");
        activity.SetTag("url.path", "/private-object");
        activity.SetTag("url.query", "signature=secret");
        activity.SetTag(
            "http.url",
            "https://example.test/object?signature=secret");
        activity.SetTag("http.target", "/object?signature=secret");
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://example.test/object?signature=secret");

        ObservabilityPrivacy.SanitizeOutboundHttp(activity, request);

        Assert.DoesNotContain(activity.TagObjects, tag =>
            tag.Key is "url.full"
                or "url.path"
                or "url.query"
                or "http.url"
                or "http.target"
            && tag.Value is not null);
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
