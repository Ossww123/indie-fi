namespace IndieFi.Web.Application.Abstractions;

/// <summary>
/// Authentication information available to application use cases.
/// </summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    long? UserId { get; }

    bool IsInRole(string role);
}
