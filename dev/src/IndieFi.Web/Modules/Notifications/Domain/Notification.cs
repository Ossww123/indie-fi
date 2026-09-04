namespace IndieFi.Web.Modules.Notifications.Domain;

public sealed class Notification
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public Guid EventId { get; set; }
    public NotificationKind Kind { get; set; }
    public string SourceType { get; set; } = string.Empty;
    public long SourceId { get; set; }
    public string? Title { get; set; }
    public string? Body { get; set; }
    public string? DestinationPath { get; set; }
    public bool IsWebVisible { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset? ContentRemovedAt { get; set; }
    public ICollection<NotificationDelivery> Deliveries { get; set; } = [];
}
