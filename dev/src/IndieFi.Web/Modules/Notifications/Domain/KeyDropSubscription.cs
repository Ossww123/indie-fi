namespace IndieFi.Web.Modules.Notifications.Domain;

public sealed class KeyDropSubscription
{
    public long UserId { get; set; }
    public long DropId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
