using System.Globalization;
using IndieFi.Web.Modules.Notifications.Domain;
using IndieFi.Web.Modules.Notifications.Infrastructure;
using Microsoft.Extensions.Options;

namespace IndieFi.Web.Modules.Notifications.Application;

public sealed class NotificationEmailTemplateRenderer
{
    private readonly Uri _publicBaseUri;

    public NotificationEmailTemplateRenderer(
        IOptions<NotificationEmailDeliveryOptions> options)
    {
        _publicBaseUri = new Uri(options.Value.PublicBaseUrl, UriKind.Absolute);
    }

    public NotificationEmailSendRequest Render(
        NotificationDelivery delivery,
        string recipientAddress)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientAddress);
        var notification = delivery.Notification
            ?? throw new ArgumentException(
                "The notification delivery must include its notification.",
                nameof(delivery));
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.Body);

        var details = BuildDetailsLine(notification.DestinationPath);
        var occurredAt = notification.OccurredAt.UtcDateTime.ToString(
            "yyyy-MM-dd HH:mm 'UTC'",
            CultureInfo.InvariantCulture);
        return new NotificationEmailSendRequest(
            delivery.MessageId,
            recipientAddress,
            $"[IndieFi] {notification.Title}",
            $"""
            {notification.Title}

            {notification.Body}
            {details}

            발생 시각: {occurredAt}
            알림 설정: {new Uri(_publicBaseUri, "/Account/Notifications")}
            """);
    }

    private string BuildDetailsLine(string? destinationPath)
    {
        if (destinationPath is null)
        {
            return string.Empty;
        }

        if (!destinationPath.StartsWith("/", StringComparison.Ordinal)
            || destinationPath.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The notification destination must be a local absolute path.",
                nameof(destinationPath));
        }

        return $"\n자세히 보기: {new Uri(_publicBaseUri, destinationPath)}";
    }
}
