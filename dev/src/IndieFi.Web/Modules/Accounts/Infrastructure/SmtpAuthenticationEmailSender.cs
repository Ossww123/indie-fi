using System.Net;
using System.Net.Mail;
using IndieFi.Web.Modules.Accounts.Application;
using Microsoft.Extensions.Options;

namespace IndieFi.Web.Modules.Accounts.Infrastructure;

public sealed class SmtpAuthenticationEmailSender : IAuthenticationEmailSender
{
    private readonly AuthenticationEmailDeliveryOptions _options;
    private readonly AuthenticationEmailMessageFactory _messageFactory;

    public SmtpAuthenticationEmailSender(
        IOptions<AuthenticationEmailDeliveryOptions> options,
        AuthenticationEmailMessageFactory messageFactory)
    {
        _options = options.Value;
        _messageFactory = messageFactory;
    }

    public async Task SendEmailConfirmationAsync(
        AuthenticationEmailDelivery delivery,
        CancellationToken cancellationToken)
    {
        using var message = _messageFactory.CreateEmailConfirmation(delivery);
        using var client = new SmtpClient(_options.Smtp.Host, _options.Smtp.Port)
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
        await client.SendMailAsync(message, timeout.Token);
    }
}
