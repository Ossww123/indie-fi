using System.Diagnostics.Metrics;
using IndieFi.Web.Modules.Media.Application;

namespace IndieFi.Web.Modules.Media.Infrastructure;

public static class MediaTelemetry
{
    public const string MeterName = "IndieFi.Media";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> ProcessingAttempts =
        Meter.CreateCounter<long>(
            "indiefi.media.processing.attempts",
            "{attempt}",
            "Media processing attempts by bounded outcome.");
    private static readonly Histogram<double> ProcessingDuration =
        Meter.CreateHistogram<double>(
            "indiefi.media.processing.duration",
            "ms",
            "Media processing attempt duration.");
    private static readonly Counter<long> RecoveryItems =
        Meter.CreateCounter<long>(
            "indiefi.media.recovery.items",
            "{media}",
            "Media rows examined by recovery batches.");
    private static readonly Counter<long> CleanupItems =
        Meter.CreateCounter<long>(
            "indiefi.media.cleanup.items",
            "{media}",
            "Media cleanup results by bounded outcome.");
    private static readonly Counter<long> OperationsObservationFailures =
        Meter.CreateCounter<long>(
            "indiefi.media.operations.observation_failures",
            "{failure}",
            "Failures while collecting media operational state.");

    private static long _processingPendingCount;
    private static long _recoveryDueCount;
    private static long _activeLeaseCount;
    private static long _expiredLeaseCount;
    private static long _cleanupBacklogCount;
    private static long _failedCount;
    private static double _oldestRecoveryDueAgeSeconds;

    static MediaTelemetry()
    {
        Gauge("indiefi.media.processing.pending", () =>
            Interlocked.Read(ref _processingPendingCount));
        Gauge("indiefi.media.recovery.due", () =>
            Interlocked.Read(ref _recoveryDueCount));
        Gauge("indiefi.media.processing.active_leases", () =>
            Interlocked.Read(ref _activeLeaseCount));
        Gauge("indiefi.media.processing.expired_leases", () =>
            Interlocked.Read(ref _expiredLeaseCount));
        Gauge("indiefi.media.cleanup.backlog", () =>
            Interlocked.Read(ref _cleanupBacklogCount));
        Gauge("indiefi.media.failed", () =>
            Interlocked.Read(ref _failedCount));
        Meter.CreateObservableGauge(
            "indiefi.media.recovery.oldest_due_age",
            () => Volatile.Read(ref _oldestRecoveryDueAgeSeconds),
            "s",
            "Age of the oldest media item due for recovery.");
    }

    public static void RecordProcessingAttempt(
        MediaProcessingAttemptOutcome outcome,
        TimeSpan duration)
    {
        var outcomeName = outcome switch
        {
            MediaProcessingAttemptOutcome.Completed => "completed",
            MediaProcessingAttemptOutcome.TransientFailure => "transient_failure",
            MediaProcessingAttemptOutcome.PermanentFailure => "permanent_failure",
            _ => "other",
        };
        var tag = new KeyValuePair<string, object?>("outcome", outcomeName);
        ProcessingAttempts.Add(1, tag);
        ProcessingDuration.Record(duration.TotalMilliseconds, tag);
    }

    public static void RecordRecoveryBatch(int examinedCount)
    {
        if (examinedCount > 0)
        {
            RecoveryItems.Add(examinedCount);
        }
    }

    public static void RecordCleanupBatch(MediaCleanupBatchResult result)
    {
        RecordCleanup("expired", result.ExpiredCount);
        RecordCleanup("ready_source_deleted", result.ReadySourceDeletedCount);
        RecordCleanup("deletion_completed", result.DeletionCompletedCount);
        RecordCleanup("tombstone_removed", result.TombstoneRemovedCount);
        RecordCleanup("storage_failure", result.FailedCount);
    }

    public static void UpdateOperationalSnapshot(MediaOperationalSnapshot snapshot)
    {
        Interlocked.Exchange(
            ref _processingPendingCount,
            snapshot.ProcessingPendingCount);
        Interlocked.Exchange(ref _recoveryDueCount, snapshot.RecoveryDueCount);
        Interlocked.Exchange(ref _activeLeaseCount, snapshot.ActiveLeaseCount);
        Interlocked.Exchange(ref _expiredLeaseCount, snapshot.ExpiredLeaseCount);
        Interlocked.Exchange(ref _cleanupBacklogCount, snapshot.CleanupBacklogCount);
        Interlocked.Exchange(ref _failedCount, snapshot.FailedCount);
        Volatile.Write(
            ref _oldestRecoveryDueAgeSeconds,
            snapshot.OldestRecoveryDueAgeSeconds);
    }

    public static void RecordOperationsObservationFailure() =>
        OperationsObservationFailures.Add(1);

    private static void RecordCleanup(string outcome, int count)
    {
        if (count > 0)
        {
            CleanupItems.Add(
                count,
                new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    private static void Gauge(string name, Func<long> observe) =>
        Meter.CreateObservableGauge(
            name,
            observe,
            "{media}",
            "Current media Worker operational state.");
}
