namespace IndieFi.Web.Modules.Accounts.Application;

public sealed record AuthenticationEmailDelivery(
    string RecipientAddress,
    string PlaintextToken,
    DateTimeOffset ExpiresAt);

public interface IAuthenticationEmailSender
{
    Task SendEmailConfirmationAsync(
        AuthenticationEmailDelivery delivery,
        CancellationToken cancellationToken);
}
