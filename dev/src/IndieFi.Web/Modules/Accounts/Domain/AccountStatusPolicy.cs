namespace IndieFi.Web.Modules.Accounts.Domain;

public enum AccountAccessState
{
    Active,
    Suspended,
    TemporarySuspensionExpired,
}

public static class AccountStatusPolicy
{
    public static void ValidateShape(
        AccountStatus status,
        DateTimeOffset? suspendedUntil)
    {
        var valid = status switch
        {
            AccountStatus.Active => suspendedUntil is null,
            AccountStatus.TemporarilySuspended => suspendedUntil is not null,
            AccountStatus.IndefinitelySuspended => suspendedUntil is null,
            _ => false,
        };

        if (!valid)
        {
            throw new InvalidOperationException(
                "The account status and suspension end do not form a valid state.");
        }
    }

    public static AccountAccessState Evaluate(
        AccountStatus status,
        DateTimeOffset? suspendedUntil,
        DateTimeOffset now)
    {
        ValidateShape(status, suspendedUntil);

        return status switch
        {
            AccountStatus.Active => AccountAccessState.Active,
            AccountStatus.TemporarilySuspended =>
                suspendedUntil <= now
                    ? AccountAccessState.TemporarySuspensionExpired
                    : AccountAccessState.Suspended,
            AccountStatus.IndefinitelySuspended =>
                AccountAccessState.Suspended,
            _ => throw new System.Diagnostics.UnreachableException(),
        };
    }

    public static bool AllowsMutation(
        AccountStatus status,
        DateTimeOffset? suspendedUntil)
    {
        ValidateShape(status, suspendedUntil);
        return status == AccountStatus.Active;
    }
}
