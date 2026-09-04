using System.Diagnostics.Metrics;
using IndieFi.Web.Modules.KeyDistributions.Infrastructure;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class WaitingRoomTelemetryTests
{
    [Fact]
    public void Metrics_ExposeBoundedOperationalTagsWithoutDropOrUserIdentifiers()
    {
        var measurements = new List<Measurement>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == WaitingRoomTelemetry.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            measurements.Add(new Measurement(
                instrument.Name,
                value,
                tags.ToArray()));
        });
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            measurements.Add(new Measurement(
                instrument.Name,
                value,
                tags.ToArray()));
        });
        listener.Start();

        WaitingRoomTelemetry.RecordOperation("join", "Created");
        WaitingRoomTelemetry.RecordTransition("promoted", 3);
        WaitingRoomTelemetry.RecordFailure("redis", "join");
        WaitingRoomTelemetry.RecordRedisDuration("join", TimeSpan.FromMilliseconds(12));

        Assert.Contains(measurements, item =>
            item.InstrumentName == "indiefi.waiting_room.operations"
            && item.Value == 1);
        Assert.Contains(measurements, item =>
            item.InstrumentName == "indiefi.waiting_room.transitions"
            && item.Value == 3);
        Assert.Contains(measurements, item =>
            item.InstrumentName == "indiefi.waiting_room.failures"
            && item.Value == 1);
        Assert.Contains(measurements, item =>
            item.InstrumentName == "indiefi.waiting_room.redis.duration"
            && item.Value == 12);
        Assert.All(measurements.SelectMany(item => item.Tags), tag => Assert.Contains(
            tag.Key,
            new[] { "operation", "outcome", "transition", "component" }));
        Assert.DoesNotContain(measurements.SelectMany(item => item.Tags), tag =>
            tag.Key.Contains("drop", StringComparison.OrdinalIgnoreCase)
            || tag.Key.Contains("user", StringComparison.OrdinalIgnoreCase)
            || tag.Key.Contains("key", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record Measurement(
        string InstrumentName,
        double Value,
        IReadOnlyList<KeyValuePair<string, object?>> Tags);
}
