using System.Diagnostics;

namespace IndieFi.Web.Infrastructure.Observability;

public static class ExternalOperationTelemetry
{
    private static readonly ActivitySource Source =
        new(ObservabilityActivitySources.External);

    public static Activity? Start(string system, string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(system);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        var activity = Source.StartActivity(
            $"{system}.{operation}",
            ActivityKind.Client);
        activity?.SetTag("external.system", system);
        activity?.SetTag("external.operation", operation);
        return activity;
    }

    public static void RecordFailure(Activity? activity, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
    }
}
