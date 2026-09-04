using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace IndieFi.Web.Modules.Notifications.Infrastructure;

public sealed class NotificationEmailDeliveryOptions
{
    public const string SectionName = "NotificationEmail";

    public bool Enabled { get; set; } = true;
    public string PublicBaseUrl { get; set; } = string.Empty;
    public TimeSpan WorkerInterval { get; set; } = TimeSpan.FromSeconds(5);
    public int BatchSize { get; set; } = 20;
    public NotificationSmtpOptions Smtp { get; set; } = new();

    public static ValidateOptionsResult Validate(
        NotificationEmailDeliveryOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();
        if (!Uri.TryCreate(options.PublicBaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp
                && baseUri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(baseUri.UserInfo)
            || !string.IsNullOrEmpty(baseUri.Query)
            || !string.IsNullOrEmpty(baseUri.Fragment)
            || baseUri.AbsolutePath != "/")
        {
            failures.Add(
                "NotificationEmail:PublicBaseUrl must be an HTTP(S) origin without "
                + "credentials, a query, or a fragment.");
        }

        if (options.WorkerInterval <= TimeSpan.Zero
            || options.WorkerInterval > TimeSpan.FromHours(1))
        {
            failures.Add(
                "NotificationEmail:WorkerInterval must be greater than zero and no "
                + "longer than one hour.");
        }

        if (options.BatchSize is < 1 or > 100)
        {
            failures.Add("NotificationEmail:BatchSize must be between 1 and 100.");
        }

        if (string.IsNullOrWhiteSpace(options.Smtp.Host))
        {
            failures.Add("NotificationEmail:Smtp:Host is required.");
        }

        if (options.Smtp.Port is < 1 or > 65535)
        {
            failures.Add("NotificationEmail:Smtp:Port must be between 1 and 65535.");
        }

        if (!MailAddress.TryCreate(options.Smtp.FromAddress, out _))
        {
            failures.Add("NotificationEmail:Smtp:FromAddress must be valid.");
        }

        if (options.Smtp.Timeout <= TimeSpan.Zero
            || options.Smtp.Timeout > TimeSpan.FromMinutes(5))
        {
            failures.Add(
                "NotificationEmail:Smtp:Timeout must be greater than zero and no "
                + "longer than five minutes.");
        }

        var hasUsername = !string.IsNullOrWhiteSpace(options.Smtp.Username);
        var hasPassword = !string.IsNullOrEmpty(options.Smtp.Password);
        if (hasUsername != hasPassword)
        {
            failures.Add(
                "NotificationEmail SMTP username and password must be configured together.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

public sealed class NotificationEmailDeliveryOptionsValidator :
    IValidateOptions<NotificationEmailDeliveryOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        NotificationEmailDeliveryOptions options) =>
        NotificationEmailDeliveryOptions.Validate(options);
}

public sealed class NotificationSmtpOptions
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 25;
    public bool EnableSsl { get; set; } = true;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = "IndieFi";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
}
