using System.Net;
using System.Net.Mail;
using IndieFi.Web.Infrastructure.Observability;
using IndieFi.Web.Modules.Notifications.Application;
using Microsoft.Extensions.Options;

namespace IndieFi.Web.Modules.Notifications.Infrastructure;

public sealed class SmtpNotificationEmailSender : INotificationEmailSender
{
    private readonly NotificationEmailDeliveryOptions _options;
    private readonly NotificationEmailMessageFactory _messageFactory;

    public SmtpNotificationEmailSender(
        IOptions<NotificationEmailDeliveryOptions> options,
        NotificationEmailMessageFactory messageFactory)
    {
        _options = options.Value;
        _messageFactory = messageFactory;
    }

    public async Task SendAsync(
        NotificationEmailSendRequest request,
        CancellationToken cancellationToken)
    {
        using var activity = ExternalOperationTelemetry.Start(
            "ses_smtp",
            "send_notification");
        using var message = _messageFactory.Create(request);
        using var client = new SmtpClient(
            _options.Smtp.Host,
            _options.Smtp.Port)
        {
            DeliveryMethod = SmtpDeliveryMethod.Network,
            EnableSsl = _options.Smtp.EnableSsl,
            UseDefaultCredentials = false,
            Timeout = checked((int)_options.Smtp.Timeout.TotalMilliseconds),
        };
        if (!string.IsNullOrWhiteSpace(_options.Smtp.Username))
        {
            client.Credentials = new NetworkCredential(
                _options.Smtp.Username,
                _options.Smtp.Password);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(_options.Smtp.Timeout);
        try
        {
            await client.SendMailAsync(message, timeout.Token);
        }
        catch (Exception exception)
        {
            ExternalOperationTelemetry.RecordFailure(activity, exception);
            throw;
        }
    }
}
