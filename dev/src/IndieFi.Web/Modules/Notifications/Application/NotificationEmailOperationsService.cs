using IndieFi.Web.Data;
using IndieFi.Web.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Notifications.Application;

public sealed record NotificationEmailQueueSummary(
    int PendingCount,
    int DueCount,
    int FailedCount,
    int ExpiredCount,
    DateTimeOffset? OldestDueAt);

public sealed record NotificationEmailFailureView(
    long DeliveryId,
    Guid MessageId,
    NotificationKind Kind,
    int AttemptCount,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset FailedAt,
    string? ErrorCode);

public enum NotificationEmailRetryOutcome
{
    Retried,
    NotFound,
    NotFailed,
}

public sealed class NotificationEmailOperationsService
{
    private readonly AppDbContext _db;

    public NotificationEmailOperationsService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<NotificationEmailQueueSummary> GetSummaryAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var email = _db.NotificationDeliveries
            .AsNoTracking()
            .Where(delivery => delivery.Channel == NotificationChannel.Email);
        var pendingCount = await email.CountAsync(
            delivery => delivery.Status == NotificationDeliveryStatus.Pending,
            cancellationToken);
        var dueCount = await email.CountAsync(
            delivery => delivery.Status == NotificationDeliveryStatus.Pending
                && delivery.NextAttemptAt <= now,
            cancellationToken);
        var failedCount = await email.CountAsync(
            delivery => delivery.Status == NotificationDeliveryStatus.Failed,
            cancellationToken);
        var expiredCount = await email.CountAsync(
            delivery => delivery.Status == NotificationDeliveryStatus.Expired,
            cancellationToken);
        var oldestDueAt = await email
            .Where(delivery => delivery.Status == NotificationDeliveryStatus.Pending
                && delivery.NextAttemptAt <= now)
            .MinAsync(
                delivery => (DateTimeOffset?)delivery.NextAttemptAt,
                cancellationToken);
        return new(
            pendingCount,
            dueCount,
            failedCount,
            expiredCount,
            oldestDueAt);
    }

    public async Task<IReadOnlyList<NotificationEmailFailureView>> GetFailuresAsync(
        long afterDeliveryId = 0,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterDeliveryId);
        if (take is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(take));
        }

        return await _db.NotificationDeliveries
            .AsNoTracking()
            .Where(delivery => delivery.Channel == NotificationChannel.Email
                && delivery.Status == NotificationDeliveryStatus.Failed
                && delivery.Id > afterDeliveryId)
            .OrderBy(delivery => delivery.Id)
            .Select(delivery => new NotificationEmailFailureView(
                delivery.Id,
                delivery.MessageId,
                delivery.Notification.Kind,
                delivery.AttemptCount,
                delivery.LastAttemptAt,
                delivery.FailedAt!.Value,
                delivery.LastErrorCode))
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    public async Task<NotificationEmailRetryOutcome> RetryFailedAsync(
        long deliveryId,
        Guid expectedMessageId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deliveryId);
        ArgumentOutOfRangeException.ThrowIfEqual(expectedMessageId, Guid.Empty);

        var updated = await _db.NotificationDeliveries
            .Where(delivery => delivery.Id == deliveryId
                && delivery.MessageId == expectedMessageId
                && delivery.Channel == NotificationChannel.Email
                && delivery.Status == NotificationDeliveryStatus.Failed)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(
                        delivery => delivery.Status,
                        NotificationDeliveryStatus.Pending)
                    .SetProperty(delivery => delivery.NextAttemptAt, now)
                    .SetProperty(
                        delivery => delivery.FailedAt,
                        (DateTimeOffset?)null),
                cancellationToken);
        if (updated == 1)
        {
            return NotificationEmailRetryOutcome.Retried;
        }

        var exists = await _db.NotificationDeliveries.AsNoTracking().AnyAsync(
            delivery => delivery.Id == deliveryId
                && delivery.MessageId == expectedMessageId
                && delivery.Channel == NotificationChannel.Email,
            cancellationToken);
        return exists
            ? NotificationEmailRetryOutcome.NotFailed
            : NotificationEmailRetryOutcome.NotFound;
    }
}
