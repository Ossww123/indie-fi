using IndieFi.Web.Modules.Notifications.Domain;

namespace IndieFi.Web.Modules.Notifications.Contracts;

public static class NotificationSourceTypes
{
    public const string ReviewAssignment = "review_assignment";
    public const string FeedbackSubmission = "feedback_submission";
    public const string KeyDrop = "key_drop";
    public const string Game = "game";
    public const string Report = "report";
    public const string User = "user";

    public static string For(NotificationKind kind) => kind switch
    {
        NotificationKind.ReviewAssigned => ReviewAssignment,
        NotificationKind.ReviewDeadlineSoon => ReviewAssignment,
        NotificationKind.ReviewExpired => ReviewAssignment,
        NotificationKind.FeedbackCompleted => FeedbackSubmission,
        NotificationKind.KeyDropOpened => KeyDrop,
        NotificationKind.FavoriteGameKeyDropCreated => KeyDrop,
        NotificationKind.FollowedUserGamePublished => Game,
        NotificationKind.ReportResolved => Report,
        NotificationKind.AccountStatusChanged => User,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
