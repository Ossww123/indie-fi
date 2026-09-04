namespace IndieFi.Web.Modules.Notifications.Domain;

public sealed class NotificationDelivery
{
    public long Id { get; set; }
    public long NotificationId { get; set; }
    public Guid MessageId { get; set; }
    public NotificationChannel Channel { get; set; }
    public NotificationDeliveryStatus Status { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? FailedAt { get; set; }
    public string? LastErrorCode { get; set; }
    public Notification Notification { get; set; } = null!;
}
