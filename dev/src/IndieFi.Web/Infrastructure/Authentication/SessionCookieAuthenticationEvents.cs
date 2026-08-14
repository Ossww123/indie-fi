using System.Globalization;
using System.Security.Claims;
using IndieFi.Web.Modules.Accounts.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace IndieFi.Web.Infrastructure.Authentication;

/// <summary>
/// Rejects an otherwise valid signed cookie when its server-owned account state is stale.
/// </summary>
public sealed class SessionCookieAuthenticationEvents : CookieAuthenticationEvents
{
    private readonly AccountSessionValidationService _sessionValidation;

    public SessionCookieAuthenticationEvents(
        AccountSessionValidationService sessionValidation)
    {
        _sessionValidation = sessionValidation;
    }

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        if (!TryReadSinglePositiveInt64(
                context.Principal,
                ClaimTypes.NameIdentifier,
                out var userId)
            || !TryReadSinglePositiveInt64(
                context.Principal,
                AuthenticationClaimTypes.SessionVersion,
                out var sessionVersion)
            || !await _sessionValidation.IsValidAsync(
                userId,
                sessionVersion,
                context.HttpContext.RequestAborted))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(context.Scheme.Name);
        }
    }

    private static bool TryReadSinglePositiveInt64(
        ClaimsPrincipal? principal,
        string claimType,
        out long value)
    {
        value = 0;
        if (principal is null)
        {
            return false;
        }

        var claims = principal.FindAll(claimType).Take(2).ToArray();
        return claims.Length == 1
            && long.TryParse(
                claims[0].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out value)
            && value > 0;
    }
}
