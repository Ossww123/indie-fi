using System.Net.Mail;
using System.Text;
using IndieFi.Web.Modules.Notifications.Application;
using Microsoft.Extensions.Options;

namespace IndieFi.Web.Modules.Notifications.Infrastructure;

public sealed class NotificationEmailMessageFactory
{
    private readonly NotificationEmailDeliveryOptions _options;

    public NotificationEmailMessageFactory(
        IOptions<NotificationEmailDeliveryOptions> options)
    {
        _options = options.Value;
    }

    public MailMessage Create(NotificationEmailSendRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfEqual(request.MessageId, Guid.Empty);

        var from = new MailAddress(
            _options.Smtp.FromAddress,
            _options.Smtp.FromName,
            Encoding.UTF8);
        var message = new MailMessage
        {
            From = from,
            Subject = request.Subject,
            SubjectEncoding = Encoding.UTF8,
            Body = request.Body,
            BodyEncoding = Encoding.UTF8,
            IsBodyHtml = false,
        };
        message.To.Add(new MailAddress(request.RecipientAddress));
        message.Headers["Message-ID"] =
            $"<{request.MessageId:N}@{from.Host.ToLowerInvariant()}>";
        return message;
    }
}
