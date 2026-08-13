namespace IndieFi.Web.Application.Abstractions;

public static class CurrentUserExtensions
{
    public static long GetRequiredUserId(this ICurrentUser currentUser)
    {
        return currentUser.UserId
            ?? throw new InvalidOperationException(
                "The authenticated user is missing a valid name identifier claim.");
    }
}
