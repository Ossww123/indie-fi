namespace IndieFi.Web.Modules.Notifications.Contracts;

/// <summary>
/// Triggers a payload-free SignalR refresh only after the projection transaction commits.
/// </summary>
public sealed record UserNotificationsChangedV1(long RecipientUserId);
