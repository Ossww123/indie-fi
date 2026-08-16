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
    private static readonly Counter<long> OperationsObservationFailures =
        Meter.CreateCounter<long>(
            "indiefi.notifications.operations.observation_failures",
            "{failure}",
            "Failures while collecting notification operational state.");

    private static long _outboxBacklogCount;
    private static double _oldestOutboxAgeSeconds;
    private static long _emailPendingCount;
    private static long _emailDueCount;
    private static long _emailFailedCount;
    private static double _oldestEmailDueAgeSeconds;
    private static long _rabbitMqReady = -1;

    static NotificationTelemetry()
    {
        Meter.CreateObservableGauge(
            "indiefi.notifications.outbox.backlog",
            () => Interlocked.Read(ref _outboxBacklogCount),
            "{message}",
            "Messages waiting in the PostgreSQL transactional Outbox.");
        Meter.CreateObservableGauge(
            "indiefi.notifications.outbox.oldest_age",
            () => Volatile.Read(ref _oldestOutboxAgeSeconds),
            "s",
            "Age of the oldest PostgreSQL Outbox message.");
        Meter.CreateObservableGauge(
            "indiefi.notifications.email.pending",
            () => Interlocked.Read(ref _emailPendingCount),
            "{delivery}",
            "Pending notification email deliveries.");
        Meter.CreateObservableGauge(
            "indiefi.notifications.email.due",
            () => Interlocked.Read(ref _emailDueCount),
            "{delivery}",
            "Due notification email deliveries.");
        Meter.CreateObservableGauge(
            "indiefi.notifications.email.failed",
            () => Interlocked.Read(ref _emailFailedCount),
            "{delivery}",
            "Unresolved failed notification email deliveries.");
        Meter.CreateObservableGauge(
            "indiefi.notifications.email.oldest_due_age",
            () => Volatile.Read(ref _oldestEmailDueAgeSeconds),
            "s",
            "Age of the oldest due notification email delivery.");
        Meter.CreateObservableGauge(
            "indiefi.notifications.rabbitmq.ready",
            () => Interlocked.Read(ref _rabbitMqReady),
            "{state}",
            "MassTransit RabbitMQ readiness (1 ready, 0 unhealthy, -1 unknown).");
    }

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

    public static void UpdateOperationalSnapshot(
        NotificationOperationalSnapshot snapshot)
    {
        Interlocked.Exchange(ref _outboxBacklogCount, snapshot.OutboxBacklogCount);
        Volatile.Write(ref _oldestOutboxAgeSeconds, snapshot.OldestOutboxAgeSeconds);
        Interlocked.Exchange(ref _emailPendingCount, snapshot.EmailPendingCount);
        Interlocked.Exchange(ref _emailDueCount, snapshot.EmailDueCount);
        Interlocked.Exchange(ref _emailFailedCount, snapshot.EmailFailedCount);
        Volatile.Write(
            ref _oldestEmailDueAgeSeconds,
            snapshot.OldestEmailDueAgeSeconds);
        Interlocked.Exchange(ref _rabbitMqReady, snapshot.RabbitMqReady ? 1 : 0);
    }

    public static void RecordOperationsObservationFailure()
    {
        Interlocked.Exchange(ref _rabbitMqReady, -1);
        OperationsObservationFailures.Add(1);
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
