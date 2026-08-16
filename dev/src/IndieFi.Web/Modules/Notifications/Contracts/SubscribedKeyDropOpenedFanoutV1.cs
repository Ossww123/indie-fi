namespace IndieFi.Web.Modules.Notifications.Contracts;

public sealed record SubscribedKeyDropOpenedFanoutV1(
    Guid FanoutId,
    long DropId,
    long AfterUserId,
    DateTimeOffset OccurredAt);
