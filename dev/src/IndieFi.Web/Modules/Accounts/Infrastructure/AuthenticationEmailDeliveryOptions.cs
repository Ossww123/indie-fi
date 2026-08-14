using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace IndieFi.Web.Modules.Accounts.Infrastructure;

public sealed class AuthenticationEmailDeliveryOptions
{
    public const string SectionName = "AuthenticationEmail";

    public bool Enabled { get; set; } = true;
    public string PublicBaseUrl { get; set; } = string.Empty;
    public TimeSpan WorkerInterval { get; set; } = TimeSpan.FromSeconds(5);
    public int BatchSize { get; set; } = 20;
    public SmtpDeliveryOptions Smtp { get; set; } = new();

    public static ValidateOptionsResult Validate(
        AuthenticationEmailDeliveryOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();
        if (!TryValidatePublicBaseUrl(options.PublicBaseUrl))
        {
            failures.Add(
                "AuthenticationEmail:PublicBaseUrl must be an HTTP(S) origin without " +
                "credentials, a query, or a fragment.");
        }

        if (options.WorkerInterval <= TimeSpan.Zero
            || options.WorkerInterval > TimeSpan.FromHours(1))
        {
            failures.Add(
                "AuthenticationEmail:WorkerInterval must be greater than zero and no " +
                "longer than one hour.");
        }

        if (options.BatchSize is < 1 or > 100)
        {
            failures.Add("AuthenticationEmail:BatchSize must be between 1 and 100.");
        }

        ValidateSmtp(options.Smtp, failures);
        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static bool TryValidatePublicBaseUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        return uri.AbsolutePath == "/";
    }

    private static void ValidateSmtp(
        SmtpDeliveryOptions? smtp,
        ICollection<string> failures)
    {
        if (smtp is null)
        {
            failures.Add("AuthenticationEmail:Smtp is required.");
            return;
        }

        if (string.IsNullOrWhiteSpace(smtp.Host))
        {
            failures.Add("AuthenticationEmail:Smtp:Host is required.");
        }

        if (smtp.Port is < 1 or > 65535)
        {
            failures.Add("AuthenticationEmail:Smtp:Port must be between 1 and 65535.");
        }

        if (!MailAddress.TryCreate(smtp.FromAddress, out _))
        {
            failures.Add("AuthenticationEmail:Smtp:FromAddress must be a valid address.");
        }

        if (smtp.Timeout <= TimeSpan.Zero || smtp.Timeout > TimeSpan.FromMinutes(5))
        {
            failures.Add(
                "AuthenticationEmail:Smtp:Timeout must be greater than zero and no " +
                "longer than five minutes.");
        }

        var hasUsername = !string.IsNullOrWhiteSpace(smtp.Username);
        var hasPassword = !string.IsNullOrEmpty(smtp.Password);
        if (hasUsername != hasPassword)
        {
            failures.Add(
                "AuthenticationEmail SMTP username and password must be configured together.");
        }
    }
}

public sealed class AuthenticationEmailDeliveryOptionsValidator
    : IValidateOptions<AuthenticationEmailDeliveryOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        AuthenticationEmailDeliveryOptions options) =>
        AuthenticationEmailDeliveryOptions.Validate(options);
}

public sealed class SmtpDeliveryOptions
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
