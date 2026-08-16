namespace IndieFi.Web.Modules.Notifications.Contracts;

/// <summary>
/// Requests one notification without carrying user-authored or sensitive content.
/// Consumers resolve display text from the source identifier after receiving it.
/// </summary>
public sealed record UserNotificationRequestedV1(
    Guid EventId,
    long RecipientUserId,
    string Kind,
    string SourceType,
    long SourceId,
    DateTimeOffset OccurredAt);
