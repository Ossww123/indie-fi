using System.Security.Claims;

namespace IndieFi.Web.Extensions;

public static class ClaimsPrincipalExtensions
{
    public static long GetUserId(this ClaimsPrincipal user)
    {
        // Callers have already enforced authentication, so a missing or malformed
        // identifier is a configuration error that must fail loudly.
        if (!long.TryParse(GetUserIdClaimValue(user), out var userId))
        {
            throw new InvalidOperationException(
                "The authenticated user is missing a valid name identifier claim.");
        }

        return userId;
    }

    public static bool TryGetUserId(this ClaimsPrincipal user, out long userId)
    {
        return long.TryParse(GetUserIdClaimValue(user), out userId);
    }

    private static string? GetUserIdClaimValue(ClaimsPrincipal user)
    {
        return user.FindFirstValue(ClaimTypes.NameIdentifier);
    }
}
