namespace IndieFi.Web.Modules.Games.Domain;

public static class GameClassificationPolicy
{
    public const int MaxTagsPerGame = 8;
    public const int MaxPlatformsPerGame = 12;
    public const int CodeMaxLength = 64;
    public const int DisplayNameMaxLength = 64;
    public const int MaxSortOrder = 10_000;

    public static bool IsValidCode(string? code)
    {
        if (string.IsNullOrEmpty(code)
            || code.Length > CodeMaxLength
            || code[0] == '-'
            || code[^1] == '-')
        {
            return false;
        }

        var previousWasHyphen = false;
        foreach (var character in code)
        {
            if (character == '-')
            {
                if (previousWasHyphen)
                {
                    return false;
                }

                previousWasHyphen = true;
                continue;
            }

            if (character is not (>= 'a' and <= 'z')
                && character is not (>= '0' and <= '9'))
            {
                return false;
            }

            previousWasHyphen = false;
        }

        return true;
    }

    public static bool IsValidDisplayName(string? displayName)
    {
        return !string.IsNullOrWhiteSpace(displayName)
            && displayName.Length <= DisplayNameMaxLength
            && string.Equals(displayName, displayName.Trim(), StringComparison.Ordinal);
    }

    public static bool IsValidSortOrder(int sortOrder)
    {
        return sortOrder is >= 0 and <= MaxSortOrder;
    }
}
