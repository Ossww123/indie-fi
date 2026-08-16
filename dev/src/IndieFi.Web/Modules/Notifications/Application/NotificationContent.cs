namespace IndieFi.Web.Modules.Notifications.Application;

public sealed record NotificationContent(
    string Title,
    string Body,
    string? DestinationPath);
