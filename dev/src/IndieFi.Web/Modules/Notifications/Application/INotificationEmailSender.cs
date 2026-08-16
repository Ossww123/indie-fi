namespace IndieFi.Web.Modules.Notifications.Application;

public sealed record NotificationEmailSendRequest(
    Guid MessageId,
    string RecipientAddress,
    string Subject,
    string Body);

public interface INotificationEmailSender
{
    Task SendAsync(
        NotificationEmailSendRequest request,
        CancellationToken cancellationToken);
}
