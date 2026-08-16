using IndieFi.Web.Data;
using IndieFi.Web.Modules.Notifications.Domain;
using IndieFi.Web.Modules.Notifications.Infrastructure;
using System.Diagnostics;

namespace IndieFi.Web.Modules.Notifications.Application;

public sealed record NotificationEmailProcessResult(
    int SentCount,
    int ExpiredCount,
    int RescheduledCount,
    int FailedCount);

public sealed class NotificationEmailDeliveryProcessor
{
    private readonly AppDbContext _db;
    private readonly NotificationEmailDeliveryAuthorityService _authority;
    private readonly INotificationEmailSender _sender;
    private readonly TimeProvider _timeProvider;

    public NotificationEmailDeliveryProcessor(
        AppDbContext db,
        NotificationEmailDeliveryAuthorityService authority,
        INotificationEmailSender sender,
        TimeProvider timeProvider)
    {
        _db = db;
        _authority = authority;
        _sender = sender;
        _timeProvider = timeProvider;
    }

    public async Task<NotificationEmailProcessResult> ProcessBatchAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var result = await ProcessBatchCoreAsync(batchSize, cancellationToken);
            NotificationTelemetry.RecordEmailBatch(
                result,
                Stopwatch.GetElapsedTime(startedAt));
            return result;
        }
        catch
        {
            NotificationTelemetry.RecordEmailBatchFault(
                Stopwatch.GetElapsedTime(startedAt));
            throw;
        }
    }

    private async Task<NotificationEmailProcessResult> ProcessBatchCoreAsync(
        int batchSize,
        CancellationToken cancellationToken)
    {
        var sent = 0;
        var expired = 0;
        var rescheduled = 0;
        var failed = 0;
        for (var index = 0; index < batchSize; index++)
        {
            var outcome = await ProcessNextAsync(cancellationToken);
            switch (outcome)
            {
                case ProcessNextOutcome.None:
                    return new(sent, expired, rescheduled, failed);
                case ProcessNextOutcome.Sent:
                    sent++;
                    break;
                case ProcessNextOutcome.Expired:
                    expired++;
                    break;
                case ProcessNextOutcome.Rescheduled:
                    rescheduled++;
                    break;
                case ProcessNextOutcome.Failed:
                    failed++;
                    break;
            }
        }

        return new(sent, expired, rescheduled, failed);
    }

    private async Task<ProcessNextOutcome> ProcessNextAsync(
        CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(
            cancellationToken);
        var now = _timeProvider.GetUtcNow();
        var delivery = await _authority.ClaimNextDueAsync(now, cancellationToken);
        if (delivery is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return ProcessNextOutcome.None;
        }

        NotificationEmailPreparation preparation;
        try
        {
            preparation = await _authority.PrepareAsync(
                delivery,
                now,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException
            || !cancellationToken.IsCancellationRequested)
        {
            delivery.AttemptCount++;
            delivery.LastAttemptAt = now;
            var outcome = ApplyFailure(
                delivery,
                NotificationEmailFailureClassifier.Classify(exception),
                now);
            await SaveAndCommitAsync(transaction, cancellationToken);
            return outcome;
        }

        if (preparation.Outcome == NotificationEmailPreparationOutcome.Expired)
        {
            delivery.Status = NotificationDeliveryStatus.Expired;
            delivery.NextAttemptAt = null;
            delivery.FailedAt = now;
            delivery.LastErrorCode = "stale_or_expired_source";
            await SaveAndCommitAsync(transaction, cancellationToken);
            return ProcessNextOutcome.Expired;
        }

        delivery.AttemptCount++;
        delivery.LastAttemptAt = now;
        try
        {
            await _sender.SendAsync(preparation.Request!, cancellationToken);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException
            || !cancellationToken.IsCancellationRequested)
        {
            var outcome = ApplyFailure(
                delivery,
                NotificationEmailFailureClassifier.Classify(exception),
                now);
            await SaveAndCommitAsync(transaction, cancellationToken);
            return outcome;
        }

        delivery.Status = NotificationDeliveryStatus.Delivered;
        delivery.NextAttemptAt = null;
        delivery.DeliveredAt = now;
        delivery.FailedAt = null;
        delivery.LastErrorCode = null;
        await SaveAndCommitAsync(transaction, cancellationToken);
        return ProcessNextOutcome.Sent;
    }

    private static ProcessNextOutcome ApplyFailure(
        NotificationDelivery delivery,
        NotificationEmailFailure failure,
        DateTimeOffset now)
    {
        if (failure.Kind == NotificationEmailFailureKind.Permanent)
        {
            delivery.Status = NotificationDeliveryStatus.Failed;
            delivery.NextAttemptAt = null;
            delivery.FailedAt = now;
            delivery.LastErrorCode = failure.ErrorCode;
            return ProcessNextOutcome.Failed;
        }

        if (delivery.AttemptCount >= NotificationPolicy.EmailMaxAttempts)
        {
            delivery.Status = NotificationDeliveryStatus.Failed;
            delivery.NextAttemptAt = null;
            delivery.FailedAt = now;
            delivery.LastErrorCode = $"retry_exhausted_{failure.ErrorCode}";
            return ProcessNextOutcome.Failed;
        }

        delivery.Status = NotificationDeliveryStatus.Pending;
        delivery.NextAttemptAt = now.Add(
            NotificationPolicy.EmailRetryDelays[delivery.AttemptCount - 1]);
        delivery.FailedAt = null;
        delivery.LastErrorCode = failure.ErrorCode;
        return ProcessNextOutcome.Rescheduled;
    }

    private async Task SaveAndCommitAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private enum ProcessNextOutcome
    {
        None,
        Sent,
        Expired,
        Rescheduled,
        Failed,
    }
}
