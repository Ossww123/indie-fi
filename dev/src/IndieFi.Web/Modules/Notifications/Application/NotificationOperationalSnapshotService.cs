using IndieFi.Web.Data;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Notifications.Application;

public sealed record NotificationOperationalSnapshot(
    int OutboxBacklogCount,
    double OldestOutboxAgeSeconds,
    int EmailPendingCount,
    int EmailDueCount,
    int EmailFailedCount,
    double OldestEmailDueAgeSeconds,
    bool RabbitMqReady);

public sealed class NotificationOperationalSnapshotService
{
    private readonly AppDbContext _db;
    private readonly NotificationEmailOperationsService _emailOperations;

    public NotificationOperationalSnapshotService(
        AppDbContext db,
        NotificationEmailOperationsService emailOperations)
    {
        _db = db;
        _emailOperations = emailOperations;
    }

    public async Task<NotificationOperationalSnapshot> GetAsync(
        DateTimeOffset now,
        bool rabbitMqReady,
        CancellationToken cancellationToken = default)
    {
        var outbox = _db.Set<OutboxMessage>().AsNoTracking();
        var outboxBacklogCount = await outbox.CountAsync(cancellationToken);
        var oldestOutboxSentAt = await outbox.MinAsync(
            message => (DateTime?)message.SentTime,
            cancellationToken);
        var email = await _emailOperations.GetSummaryAsync(now, cancellationToken);
        return new(
            outboxBacklogCount,
            AgeSeconds(now, oldestOutboxSentAt),
            email.PendingCount,
            email.DueCount,
            email.FailedCount,
            AgeSeconds(now, email.OldestDueAt),
            rabbitMqReady);
    }

    private static double AgeSeconds(
        DateTimeOffset now,
        DateTime? value) => value is null
            ? 0
            : Math.Max(0, (now - new DateTimeOffset(
                DateTime.SpecifyKind(value.Value, DateTimeKind.Utc))).TotalSeconds);

    private static double AgeSeconds(
        DateTimeOffset now,
        DateTimeOffset? value) => value is null
            ? 0
            : Math.Max(0, (now - value.Value).TotalSeconds);
}
