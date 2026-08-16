namespace IndieFi.Web.Modules.Notifications.Domain;

public sealed record NotificationChannelDefaults(
    bool WebEnabled,
    bool EmailEnabled,
    bool IsMandatory);

public static class NotificationPolicy
{
    public const int TitleMaxLength = 160;
    public const int BodyMaxLength = 500;
    public const int DestinationPathMaxLength = 512;
    public const int SourceTypeMaxLength = 40;
    public const int ErrorCodeMaxLength = 64;

    public static readonly TimeSpan ReviewDeadlineLeadTime =
        TimeSpan.FromMinutes(10);

    public static readonly TimeSpan WebRetention = TimeSpan.FromDays(90);
    public static readonly TimeSpan PublishedOutboxRetention = TimeSpan.FromDays(7);
    public static readonly TimeSpan ConsumerDeduplicationRetention = TimeSpan.FromDays(30);
    public static readonly TimeSpan ResolvedFailureRetention = TimeSpan.FromDays(30);

    public static IReadOnlyList<TimeSpan> EmailRetryDelays { get; } =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(6),
    ];

    public static IReadOnlyList<TimeSpan> ConsumerRetryDelays { get; } =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
    ];

    public static NotificationChannelDefaults GetDefaults(NotificationKind kind)
    {
        return kind switch
        {
            NotificationKind.ReviewAssigned => new(true, true, false),
            NotificationKind.ReviewDeadlineSoon => new(true, true, false),
            NotificationKind.ReviewExpired => new(true, false, false),
            NotificationKind.FeedbackCompleted => new(true, true, false),
            NotificationKind.KeyDropOpened => new(true, true, false),
            NotificationKind.FavoriteGameKeyDropCreated => new(true, false, false),
            NotificationKind.FollowedUserGamePublished => new(true, false, false),
            NotificationKind.ReportResolved => new(true, true, false),
            NotificationKind.AccountStatusChanged => new(true, true, true),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }
}
