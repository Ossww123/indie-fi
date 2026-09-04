using System.Security.Claims;
using IndieFi.Web.Application.Abstractions;

namespace IndieFi.Web.Infrastructure.Authentication;

/// <summary>
/// Reads the current user without exposing HTTP concerns to application use cases.
/// </summary>
public sealed class HttpCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpCurrentUser(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public bool IsAuthenticated => Principal.Identity?.IsAuthenticated == true;

    public long? UserId
    {
        get
        {
            var value = Principal.FindFirstValue(ClaimTypes.NameIdentifier);
            return long.TryParse(value, out var userId) ? userId : null;
        }
    }

    public bool IsInRole(string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        return Principal.IsInRole(role);
    }

    private ClaimsPrincipal Principal =>
        _httpContextAccessor.HttpContext?.User ?? new ClaimsPrincipal();
}
