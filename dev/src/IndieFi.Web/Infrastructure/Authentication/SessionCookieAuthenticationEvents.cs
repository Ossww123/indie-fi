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
                out var sessionVersion))
        {
            await RejectAsync(context);
            return;
        }

        var validation = await _sessionValidation.ValidateAsync(
            userId,
            sessionVersion,
            context.HttpContext.RequestAborted);
        if (validation is null)
        {
            await RejectAsync(context);
            return;
        }

        RefreshDisplayName(context, validation.DisplayName);
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(context.Scheme.Name);
    }

    private static void RefreshDisplayName(
        CookieValidatePrincipalContext context,
        string displayName)
    {
        var currentNames = context.Principal!.FindAll(ClaimTypes.Name).ToArray();
        if (currentNames.Length == 1
            && string.Equals(
                currentNames[0].Value,
                displayName,
                StringComparison.Ordinal))
        {
            return;
        }

        var principal = context.Principal.Clone();
        var identity = principal.Identity as ClaimsIdentity
            ?? throw new InvalidOperationException(
                "The application cookie must contain a claims identity.");
        foreach (var claim in identity.FindAll(ClaimTypes.Name).ToArray())
        {
            identity.RemoveClaim(claim);
        }

        identity.AddClaim(new Claim(ClaimTypes.Name, displayName));
        context.ReplacePrincipal(principal);
        context.ShouldRenew = true;
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
