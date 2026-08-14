using System.Globalization;
using System.Security.Claims;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace IndieFi.Web.Infrastructure.Authentication;

/// <summary>
/// Issues the application session shared by every successful login method.
/// </summary>
public sealed class AccountSignInService
{
    public Task SignInAsync(
        HttpContext httpContext,
        long userId,
        long sessionVersion)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sessionVersion);

        var userIdValue = userId.ToString(CultureInfo.InvariantCulture);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userIdValue),
            new(ClaimTypes.Name, UserDisplayIdentifier.Format(userId)),
            new(
                AuthenticationClaimTypes.SessionVersion,
                sessionVersion.ToString(CultureInfo.InvariantCulture)),
        };
        var identity = new ClaimsIdentity(
            claims,
            CookieAuthenticationDefaults.AuthenticationScheme);

        return httpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity));
    }
}
