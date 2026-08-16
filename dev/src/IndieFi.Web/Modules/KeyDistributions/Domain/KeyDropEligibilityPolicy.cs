namespace IndieFi.Web.Modules.KeyDistributions.Domain;

public static class KeyDropEligibilityPolicy
{
    public const int MinimumAccountAgeDays = 1;
    public const int MaximumAccountAgeDays = 365;
    public const int MinimumCompletedReviewCount = 1;
    public const int MaximumCompletedReviewCount = 100;

    public static bool IsValidAccountAgeDays(int? value) =>
        value is null or >= MinimumAccountAgeDays and <= MaximumAccountAgeDays;

    public static bool IsValidCompletedReviewCount(int? value) =>
        value is null or >= MinimumCompletedReviewCount and <= MaximumCompletedReviewCount;

    public static KeyDropEligibilityOutcome Evaluate(
        KeyDropEligibilitySnapshot snapshot)
    {
        if (!snapshot.DropExists)
        {
            return KeyDropEligibilityOutcome.DropNotFound;
        }

        if (!snapshot.AccountAvailable || snapshot.AccountCreatedAt is null)
        {
            return KeyDropEligibilityOutcome.AccountUnavailable;
        }

        if (snapshot.MinimumAccountAgeDays is int minimumAgeDays
            && snapshot.AccountCreatedAt.Value
                > snapshot.DatabaseNow.AddDays(-minimumAgeDays))
        {
            return KeyDropEligibilityOutcome.AccountTooNew;
        }

        if (snapshot.MinimumCompletedReviewCount is int minimumReviewCount
            && snapshot.CompletedReviewCount < minimumReviewCount)
        {
            return KeyDropEligibilityOutcome.NotEnoughCompletedReviews;
        }

        return KeyDropEligibilityOutcome.Eligible;
    }
}

public enum KeyDropEligibilityOutcome
{
    Eligible,
    DropNotFound,
    AccountUnavailable,
    AccountTooNew,
    NotEnoughCompletedReviews,
}

public sealed record KeyDropEligibilitySnapshot(
    bool DropExists,
    bool AccountAvailable,
    int? MinimumAccountAgeDays,
    int? MinimumCompletedReviewCount,
    DateTimeOffset? AccountCreatedAt,
    int CompletedReviewCount,
    DateTimeOffset DatabaseNow);
