namespace IndieFi.Web.Modules.Notifications.Domain;

public enum NotificationDeliveryStatus
{
    Suppressed = 1,
    Pending = 2,
    Delivered = 3,
    Failed = 4,
    Expired = 5,
}
