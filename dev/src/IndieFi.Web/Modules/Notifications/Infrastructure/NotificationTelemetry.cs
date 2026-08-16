using System.Diagnostics.Metrics;
using IndieFi.Web.Modules.Notifications.Application;

namespace IndieFi.Web.Modules.Notifications.Infrastructure;

public static class NotificationTelemetry
{
    public const string MeterName = "IndieFi.Notifications";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> ConsumerMessages = Meter.CreateCounter<long>(
        "indiefi.notifications.consumer.messages",
        "{message}",
        "Notification messages consumed by bounded contract and outcome.");
    private static readonly Counter<long> ConsumerRetries = Meter.CreateCounter<long>(
        "indiefi.notifications.consumer.retries",
        "{retry}",
        "Notification message retry attempts by bounded contract.");
    private static readonly Histogram<double> ConsumerDuration = Meter.CreateHistogram<double>(
        "indiefi.notifications.consumer.duration",
        "ms",
        "End-to-end notification consumer duration including in-memory retries.");
    private static readonly Counter<long> EmailDeliveries = Meter.CreateCounter<long>(
        "indiefi.notifications.email.deliveries",
        "{delivery}",
        "Notification email delivery outcomes.");
    private static readonly Histogram<double> EmailBatchDuration = Meter.CreateHistogram<double>(
        "indiefi.notifications.email.batch.duration",
        "ms",
        "Notification email batch processing duration.");

    public static void RecordConsumer<T>(
        string outcome,
        TimeSpan duration)
        where T : class
    {
        var contract = GetContractName<T>();
        ConsumerMessages.Add(
            1,
            new("contract", contract),
            new("outcome", outcome));
        ConsumerDuration.Record(
            duration.TotalMilliseconds,
            new KeyValuePair<string, object?>("contract", contract),
            new KeyValuePair<string, object?>("outcome", outcome));
    }

    public static void RecordConsumerRetry() => ConsumerRetries.Add(1);

    public static void RecordEmailBatch(
        NotificationEmailProcessResult result,
        TimeSpan duration)
    {
        RecordEmailOutcome("sent", result.SentCount);
        RecordEmailOutcome("expired", result.ExpiredCount);
        RecordEmailOutcome("rescheduled", result.RescheduledCount);
        RecordEmailOutcome("failed", result.FailedCount);
        EmailBatchDuration.Record(duration.TotalMilliseconds);
    }

    public static void RecordEmailBatchFault(TimeSpan duration)
    {
        RecordEmailOutcome("batch_fault", 1);
        EmailBatchDuration.Record(duration.TotalMilliseconds);
    }

    private static void RecordEmailOutcome(string outcome, int count)
    {
        if (count > 0)
        {
            EmailDeliveries.Add(
                count,
                new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    private static string GetContractName<T>() => typeof(T).Name switch
    {
        "UserNotificationRequestedV1" => "user_notification_requested_v1",
        "UserNotificationsChangedV1" => "user_notifications_changed_v1",
        "FavoriteGameKeyDropCreatedFanoutV1" =>
            "favorite_game_key_drop_created_fanout_v1",
        "SubscribedKeyDropOpenedFanoutV1" =>
            "subscribed_key_drop_opened_fanout_v1",
        "FollowedUserGamePublishedFanoutV1" =>
            "followed_user_game_published_fanout_v1",
        _ => "other",
    };
}
