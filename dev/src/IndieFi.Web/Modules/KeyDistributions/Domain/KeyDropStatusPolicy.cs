namespace IndieFi.Web.Modules.KeyDistributions.Domain;

public enum KeyDropStatus
{
    Scheduled,
    Active,
    SoldOut,
    ClosedEarly,
}

public static class KeyDropStatusPolicy
{
    public static KeyDropStatus Resolve(
        DateTimeOffset opensAt,
        int totalKeys,
        int remainingKeys,
        DateTimeOffset? closedAt,
        DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalKeys);
        ArgumentOutOfRangeException.ThrowIfNegative(remainingKeys);
        if (remainingKeys > totalKeys)
        {
            throw new ArgumentOutOfRangeException(
                nameof(remainingKeys),
                "Remaining keys cannot exceed total keys.");
        }

        if (closedAt is not null)
        {
            return KeyDropStatus.ClosedEarly;
        }

        if (remainingKeys == 0)
        {
            return KeyDropStatus.SoldOut;
        }

        return opensAt > now
            ? KeyDropStatus.Scheduled
            : KeyDropStatus.Active;
    }

    public static bool CanAddInventory(KeyDropStatus status) =>
        status is KeyDropStatus.Scheduled or KeyDropStatus.Active;

    public static bool CanClaim(KeyDropStatus status) =>
        status == KeyDropStatus.Active;

    public static bool CanCloseEarly(KeyDropStatus status) =>
        status is KeyDropStatus.Scheduled or KeyDropStatus.Active;
}
