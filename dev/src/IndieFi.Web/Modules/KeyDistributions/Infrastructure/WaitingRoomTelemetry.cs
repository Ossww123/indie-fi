using System.Diagnostics.Metrics;

namespace IndieFi.Web.Modules.KeyDistributions.Infrastructure;

public static class WaitingRoomTelemetry
{
    public const string MeterName = "IndieFi.WaitingRoom";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Operations = Meter.CreateCounter<long>(
        "indiefi.waiting_room.operations",
        "{operation}",
        "Completed waiting-room operations by bounded outcome.");
    private static readonly Counter<long> Transitions = Meter.CreateCounter<long>(
        "indiefi.waiting_room.transitions",
        "{entry}",
        "Waiting-room entry state transitions.");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>(
        "indiefi.waiting_room.failures",
        "{failure}",
        "Redis and SignalR failures by bounded component and operation.");
    private static readonly Histogram<double> RedisDuration = Meter.CreateHistogram<double>(
        "indiefi.waiting_room.redis.duration",
        "ms",
        "Redis waiting-room operation duration.");

    public static void RecordOperation(string operation, string outcome) =>
        Operations.Add(1, new("operation", operation), new("outcome", outcome));

    public static void RecordTransition(string transition, int count = 1)
    {
        if (count > 0)
        {
            Transitions.Add(
                count,
                new KeyValuePair<string, object?>("transition", transition));
        }
    }

    public static void RecordFailure(string component, string operation) =>
        Failures.Add(1, new("component", component), new("operation", operation));

    public static void RecordRedisDuration(string operation, TimeSpan elapsed) =>
        RedisDuration.Record(
            elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("operation", operation));
}
