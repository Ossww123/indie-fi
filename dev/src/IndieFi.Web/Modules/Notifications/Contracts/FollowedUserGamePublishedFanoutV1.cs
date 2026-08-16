namespace IndieFi.Web.Modules.Notifications.Contracts;

public sealed record FollowedUserGamePublishedFanoutV1(
    Guid FanoutId,
    long GameId,
    long AfterUserId,
    DateTimeOffset OccurredAt);
