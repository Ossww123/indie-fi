using System.Security.Claims;
using IndieFi.Web.Modules.Accounts.Domain;

namespace IndieFi.Web.Infrastructure.Authentication;

public static class GoogleExternalIdentityClaims
{
    public static bool TryRead(
        ClaimsPrincipal? principal,
        out string? subject,
        out AccountEmailAddress? email)
    {
        subject = null;
        email = null;

        if (!TryReadSingleClaim(principal, ClaimTypes.NameIdentifier, out var candidateSubject)
            || !ExternalLoginPolicy.IsValidProviderUserId(candidateSubject)
            || !TryReadSingleClaim(principal, ClaimTypes.Email, out var candidateEmail)
            || !TryReadSingleClaim(
                principal,
                ExternalAuthenticationDefaults.GoogleEmailVerifiedClaim,
                out var emailVerified)
            || !bool.TryParse(emailVerified, out var isEmailVerified)
            || !isEmailVerified
            || !AccountEmailAddressPolicy.TryCreate(candidateEmail, out email, out _))
        {
            return false;
        }

        subject = candidateSubject;
        return true;
    }

    private static bool TryReadSingleClaim(
        ClaimsPrincipal? principal,
        string claimType,
        out string? value)
    {
        value = null;
        if (principal is null)
        {
            return false;
        }

        var claims = principal.FindAll(claimType).Take(2).ToArray();
        if (claims.Length != 1 || string.IsNullOrWhiteSpace(claims[0].Value))
        {
            return false;
        }

        value = claims[0].Value;
        return true;
    }
}
