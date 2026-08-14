namespace IndieFi.Web.Modules.Accounts.Domain;

public enum AccountAccessState
{
    Active,
    Suspended,
    TemporarySuspensionExpired,
}

public static class AccountStatusPolicy
{
    public static AccountAccessState Evaluate(
        AccountStatus status,
        DateTimeOffset? suspendedUntil,
        DateTimeOffset now)
    {
        return status switch
        {
            AccountStatus.Active when suspendedUntil is null =>
                AccountAccessState.Active,
            AccountStatus.TemporarilySuspended when suspendedUntil is not null =>
                suspendedUntil <= now
                    ? AccountAccessState.TemporarySuspensionExpired
                    : AccountAccessState.Suspended,
            AccountStatus.IndefinitelySuspended when suspendedUntil is null =>
                AccountAccessState.Suspended,
            _ => throw new InvalidOperationException(
                "The account status and suspension end do not form a valid state."),
        };
    }
}
