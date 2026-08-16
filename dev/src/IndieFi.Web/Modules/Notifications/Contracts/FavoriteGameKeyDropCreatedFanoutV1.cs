namespace IndieFi.Web.Modules.Notifications.Contracts;

/// <summary>
/// Pages through favorite-game recipients without putting the recipient set on the broker.
/// </summary>
public sealed record FavoriteGameKeyDropCreatedFanoutV1(
    Guid FanoutId,
    long DropId,
    long AfterUserId,
    DateTimeOffset OccurredAt);
