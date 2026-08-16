using System.Diagnostics.Metrics;
using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Contracts;
using IndieFi.Web.Modules.Notifications.Infrastructure;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class NotificationTelemetryTests
{
    [Fact]
    public void Metrics_ExposeBoundedContractsAndOutcomesWithoutIdentifiers()
    {
        var measurements = new List<Measurement>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == NotificationTelemetry.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            measurements.Add(new(
                instrument.Name,
                value,
                tags.ToArray()));
        });
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            measurements.Add(new(
                instrument.Name,
                value,
                tags.ToArray()));
        });
        listener.Start();

        NotificationTelemetry.RecordConsumer<UserNotificationRequestedV1>(
            "succeeded",
            TimeSpan.FromMilliseconds(12));
        NotificationTelemetry.RecordConsumerRetry();
        NotificationTelemetry.RecordConsumer<UnknownContract>(
            "failed",
            TimeSpan.FromMilliseconds(20));
        NotificationTelemetry.RecordEmailBatch(
            new NotificationEmailProcessResult(2, 1, 3, 1),
            TimeSpan.FromMilliseconds(30));
        NotificationTelemetry.RecordEmailBatchFault(
            TimeSpan.FromMilliseconds(40));

        Assert.Contains(measurements, item =>
            item.InstrumentName == "indiefi.notifications.consumer.messages"
            && item.Value == 1
            && item.HasTag("contract", "user_notification_requested_v1")
            && item.HasTag("outcome", "succeeded"));
        Assert.Contains(measurements, item =>
            item.InstrumentName == "indiefi.notifications.consumer.retries"
            && item.Value == 1);
        Assert.Contains(measurements, item =>
            item.InstrumentName == "indiefi.notifications.consumer.duration"
            && item.Value == 20
            && item.HasTag("contract", "other"));
        Assert.Contains(measurements, item =>
            item.InstrumentName == "indiefi.notifications.email.deliveries"
            && item.Value == 3
            && item.HasTag("outcome", "rescheduled"));
        Assert.Contains(measurements, item =>
            item.InstrumentName == "indiefi.notifications.email.deliveries"
            && item.Value == 1
            && item.HasTag("outcome", "batch_fault"));
        Assert.Contains(measurements, item =>
            item.InstrumentName == "indiefi.notifications.email.batch.duration"
            && item.Value == 30);
        Assert.All(
            measurements.SelectMany(item => item.Tags),
            tag => Assert.Contains(tag.Key, new[] { "contract", "outcome" }));
        Assert.DoesNotContain(
            measurements.SelectMany(item => item.Tags),
            tag => tag.Key.Contains("user", StringComparison.OrdinalIgnoreCase)
                || tag.Key.Contains("message_id", StringComparison.OrdinalIgnoreCase)
                || tag.Key.Contains("event", StringComparison.OrdinalIgnoreCase)
                || tag.Key.Contains("email", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record UnknownContract;

    private sealed record Measurement(
        string InstrumentName,
        double Value,
        IReadOnlyList<KeyValuePair<string, object?>> Tags)
    {
        public bool HasTag(string key, string value) => Tags.Any(
            tag => tag.Key == key && Equals(tag.Value, value));
    }
}
