namespace IndieFi.Web.Modules.Notifications.Domain;

public sealed class NotificationPreference
{
    public long UserId { get; set; }
    public NotificationKind Kind { get; set; }
    public bool WebEnabled { get; set; }
    public bool EmailEnabled { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
