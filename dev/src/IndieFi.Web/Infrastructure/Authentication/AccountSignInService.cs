using System.Globalization;
using System.Security.Claims;
using IndieFi.Web.Modules.Profiles.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace IndieFi.Web.Infrastructure.Authentication;

/// <summary>
/// Issues the application session shared by every successful login method.
/// </summary>
public sealed class AccountSignInService
{
    private readonly UserDisplayNameResolver _displayNames;

    public AccountSignInService(UserDisplayNameResolver displayNames)
    {
        _displayNames = displayNames;
    }

    public async Task SignInAsync(
        HttpContext httpContext,
        long userId,
        long sessionVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sessionVersion);

        var userIdValue = userId.ToString(CultureInfo.InvariantCulture);
        var displayName = await _displayNames.ResolveAsync(userId, cancellationToken);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userIdValue),
            new(ClaimTypes.Name, displayName),
            new(
                AuthenticationClaimTypes.SessionVersion,
                sessionVersion.ToString(CultureInfo.InvariantCulture)),
        };
        var identity = new ClaimsIdentity(
            claims,
            CookieAuthenticationDefaults.AuthenticationScheme);

        await httpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity));
    }
}
