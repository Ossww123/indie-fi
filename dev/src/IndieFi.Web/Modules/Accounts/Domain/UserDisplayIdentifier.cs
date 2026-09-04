using System.Globalization;

namespace IndieFi.Web.Modules.Accounts.Domain;

public static class UserDisplayIdentifier
{
    public static string Format(long userId)
    {
        if (userId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(userId),
                "A display identifier requires a persisted user ID.");
        }

        return $"user#{userId.ToString(CultureInfo.InvariantCulture)}";
    }
}
