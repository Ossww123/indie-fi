using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Domain;

namespace IndieFi.Web.Tests;

internal sealed record StagedNotificationEvent(
    Guid EventId,
    long RecipientUserId,
    NotificationKind Kind,
    string SourceType,
    long SourceId,
    DateTimeOffset OccurredAt);

internal sealed class TestNotificationEventPublisher : INotificationEventPublisher
{
    public List<StagedNotificationEvent> Events { get; } = [];
    public Exception? Failure { get; init; }

    public Task StageAsync(
        Guid eventId,
        long recipientUserId,
        NotificationKind kind,
        string sourceType,
        long sourceId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        if (Failure is not null)
        {
            return Task.FromException(Failure);
        }

        Events.Add(new StagedNotificationEvent(
            eventId,
            recipientUserId,
            kind,
            sourceType,
            sourceId,
            occurredAt));
        return Task.CompletedTask;
    }
}
