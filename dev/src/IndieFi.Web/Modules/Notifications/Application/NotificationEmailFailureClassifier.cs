using System.Net.Mail;
using System.Security.Cryptography;

namespace IndieFi.Web.Modules.Notifications.Application;

public enum NotificationEmailFailureKind
{
    Transient,
    Permanent,
}

public sealed record NotificationEmailFailure(
    NotificationEmailFailureKind Kind,
    string ErrorCode);

public static class NotificationEmailFailureClassifier
{
    public static NotificationEmailFailure Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is FormatException)
        {
            return new(NotificationEmailFailureKind.Permanent, "invalid_recipient");
        }

        if (exception is CryptographicException)
        {
            return new(
                NotificationEmailFailureKind.Permanent,
                "recipient_decryption_failed");
        }

        if (exception is ArgumentException)
        {
            return new(
                NotificationEmailFailureKind.Permanent,
                "invalid_notification_content");
        }

        if (exception is SmtpException smtpException
            && (int)smtpException.StatusCode is >= 500 and < 600)
        {
            return new(NotificationEmailFailureKind.Permanent, "smtp_permanent_rejection");
        }

        if (exception is OperationCanceledException)
        {
            return new(NotificationEmailFailureKind.Transient, "smtp_timeout");
        }

        return new(NotificationEmailFailureKind.Transient, "smtp_transient_failure");
    }
}
