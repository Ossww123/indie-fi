namespace IndieFi.Web.Modules.Accounts.Domain;

public static class UserDisplayName
{
    public static string Resolve(long userId, string? nickname) =>
        string.IsNullOrWhiteSpace(nickname)
            ? UserDisplayIdentifier.Format(userId)
            : nickname;
}
