namespace IndieFi.Web.Modules.Notifications.Domain;

public enum NotificationKind
{
    ReviewAssigned = 1,
    ReviewDeadlineSoon = 2,
    ReviewExpired = 3,
    FeedbackCompleted = 4,
    KeyDropOpened = 5,
    FavoriteGameKeyDropCreated = 6,
    FollowedUserGamePublished = 7,
    ReportResolved = 8,
    AccountStatusChanged = 9,
}
