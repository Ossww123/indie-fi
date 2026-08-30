using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace IndieFi.Web.Infrastructure.Observability;

public static class ObservabilityModule
{
    public static IServiceCollection AddIndieFiObservability(
        this IServiceCollection services,
        ILoggingBuilder logging,
        IConfiguration configuration,
        IHostEnvironment environment,
        string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        services.AddOptions<ObservabilityOptions>()
            .Bind(configuration.GetSection(ObservabilityOptions.SectionName))
            .Validate(
                ObservabilityOptions.IsValid,
                "Observability settings are invalid.")
            .ValidateOnStart();

        var options = configuration
            .GetSection(ObservabilityOptions.SectionName)
            .Get<ObservabilityOptions>() ?? new ObservabilityOptions();
        if (!options.Enabled)
        {
            return services;
        }

        if (!ObservabilityOptions.IsValid(options)
            || !ObservabilityOptions.TryGetSafeEndpoint(
                options.OtlpEndpoint,
                out var endpoint)
            || endpoint is null)
        {
            throw new InvalidOperationException(
                "Observability settings are invalid while telemetry is enabled.");
        }

        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
        Activity.ForceDefaultIdFormat = true;

        var serviceVersion = string.IsNullOrEmpty(options.ServiceVersion)
            ? EntryAssemblyVersion()
            : options.ServiceVersion;
        var instanceId = string.IsNullOrEmpty(options.InstanceId)
            ? $"{Environment.MachineName}:{Environment.ProcessId}"
            : options.InstanceId;

        ResourceBuilder CreateResource() => ResourceBuilder
            .CreateDefault()
            .AddService(
                serviceName,
                serviceNamespace: "IndieFi",
                serviceVersion: serviceVersion,
                serviceInstanceId: instanceId)
            .AddAttributes(
            [
                new KeyValuePair<string, object>(
                    "deployment.environment.name",
                    environment.EnvironmentName),
            ]);

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName,
                    serviceNamespace: "IndieFi",
                    serviceVersion: serviceVersion,
                    serviceInstanceId: instanceId)
                .AddAttributes(
                [
                    new KeyValuePair<string, object>(
                        "deployment.environment.name",
                        environment.EnvironmentName),
                ]))
            .WithMetrics(metrics =>
            {
                metrics
                    .AddRuntimeInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddNpgsqlInstrumentation()
                    .AddMeter(
                        "IndieFi.Feedback",
                        "IndieFi.Media",
                        "IndieFi.Notifications",
                        "IndieFi.WaitingRoom")
                    .AddOtlpExporter(exporter => ConfigureExporter(exporter, endpoint));

                if (serviceName == ObservabilityServiceNames.Web)
                {
                    metrics.AddAspNetCoreInstrumentation();
                }
            })
            .WithTracing(tracing =>
            {
                tracing
                    .SetSampler(new ParentBasedSampler(
                        new TraceIdRatioBasedSampler(options.TraceSamplingRatio)))
                    .AddSource(ObservabilityActivitySources.Redis)
                    .AddNpgsql()
                    .AddHttpClientInstrumentation(instrumentation =>
                    {
                        instrumentation.EnrichWithHttpRequestMessage =
                            ObservabilityPrivacy.SanitizeOutboundHttp;
                    })
                    .AddOtlpExporter(exporter => ConfigureExporter(exporter, endpoint));

                if (serviceName == ObservabilityServiceNames.Web)
                {
                    tracing.AddAspNetCoreInstrumentation(instrumentation =>
                    {
                        instrumentation.Filter =
                            ObservabilityPrivacy.ShouldTraceInboundHttp;
                        instrumentation.EnrichWithHttpRequest =
                            ObservabilityPrivacy.SanitizeInboundHttp;
                    });
                }
            });

        logging.AddOpenTelemetry(logs =>
        {
            logs.SetResourceBuilder(CreateResource());
            logs.IncludeScopes = true;
            // Export the message template and scope, but do not duplicate rendered
            // values. Request bodies, headers, query strings, and exception data are
            // never added here; individual instrumentations must keep that boundary.
            logs.IncludeFormattedMessage = false;
            logs.ParseStateValues = false;
            logs.AddOtlpExporter(exporter => ConfigureExporter(exporter, endpoint));
        });

        return services;
    }

    private static void ConfigureExporter(
        OtlpExporterOptions exporter,
        Uri endpoint)
    {
        exporter.Endpoint = endpoint;
        exporter.Protocol = OtlpExportProtocol.Grpc;
        exporter.TimeoutMilliseconds = 5_000;
    }

    private static string EntryAssemblyVersion()
    {
        var assembly = Assembly.GetEntryAssembly();
        return assembly?
                   .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                   .InformationalVersion
               ?? assembly?.GetName().Version?.ToString()
               ?? "unknown";
    }
}
