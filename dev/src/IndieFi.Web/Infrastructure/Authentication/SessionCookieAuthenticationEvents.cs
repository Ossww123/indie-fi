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

    public override Task RedirectToLogin(
        RedirectContext<CookieAuthenticationOptions> context)
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            SetPrivateApiHeaders(context.Response);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return context.Response.WriteAsJsonAsync(
                new
                {
                    code = "authentication_required",
                    message = "Authentication is required.",
                },
                context.HttpContext.RequestAborted);
        }

        return base.RedirectToLogin(context);
    }

    public override Task RedirectToAccessDenied(
        RedirectContext<CookieAuthenticationOptions> context)
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            SetPrivateApiHeaders(context.Response);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return context.Response.WriteAsJsonAsync(
                new
                {
                    code = "access_denied",
                    message = "Access is denied.",
                },
                context.HttpContext.RequestAborted);
        }

        return base.RedirectToAccessDenied(context);
    }

    private static void SetPrivateApiHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers["Referrer-Policy"] = "no-referrer";
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
