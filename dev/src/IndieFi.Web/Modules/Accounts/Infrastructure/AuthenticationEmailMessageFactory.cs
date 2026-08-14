using System.Globalization;
using System.Net.Mail;
using System.Text;
using IndieFi.Web.Modules.Accounts.Application;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace IndieFi.Web.Modules.Accounts.Infrastructure;

public sealed class AuthenticationEmailMessageFactory
{
    private readonly AuthenticationEmailDeliveryOptions _options;

    public AuthenticationEmailMessageFactory(
        IOptions<AuthenticationEmailDeliveryOptions> options)
    {
        _options = options.Value;
    }

    public MailMessage CreateEmailConfirmation(AuthenticationEmailDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentException.ThrowIfNullOrEmpty(delivery.PlaintextToken);

        var confirmationPage = new Uri(
            new Uri(_options.PublicBaseUrl, UriKind.Absolute),
            "/ConfirmEmail");
        var confirmationLink = QueryHelpers.AddQueryString(
            confirmationPage.AbsoluteUri,
            "token",
            delivery.PlaintextToken);
        var expiresAt = delivery.ExpiresAt.UtcDateTime.ToString(
            "yyyy-MM-dd HH:mm 'UTC'",
            CultureInfo.InvariantCulture);

        var message = new MailMessage
        {
            From = new MailAddress(
                _options.Smtp.FromAddress,
                _options.Smtp.FromName,
                Encoding.UTF8),
            Subject = "IndieFi 이메일 주소 확인",
            SubjectEncoding = Encoding.UTF8,
            Body = $"""
                IndieFi 이메일 주소 확인

                아래 링크를 열어 이메일 주소를 확인해 주세요.
                {confirmationLink}

                이 링크는 {expiresAt}까지 유효합니다.
                본인이 요청하지 않았다면 이 메일을 무시해 주세요.
                """,
            BodyEncoding = Encoding.UTF8,
            IsBodyHtml = false,
        };
        message.To.Add(new MailAddress(delivery.RecipientAddress));
        return message;
    }
}
