using IndieFi.Web.Modules.KeyDistributions.Domain;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class KeyDropEligibilityPolicyTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null, true)]
    [InlineData(1, true)]
    [InlineData(365, true)]
    [InlineData(0, false)]
    [InlineData(366, false)]
    public void AccountAgeRange_IsOptionalAndBounded(int? value, bool expected)
    {
        Assert.Equal(expected, KeyDropEligibilityPolicy.IsValidAccountAgeDays(value));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(1, true)]
    [InlineData(100, true)]
    [InlineData(0, false)]
    [InlineData(101, false)]
    public void CompletedReviewRange_IsOptionalAndBounded(int? value, bool expected)
    {
        Assert.Equal(
            expected,
            KeyDropEligibilityPolicy.IsValidCompletedReviewCount(value));
    }

    [Fact]
    public void Evaluate_WhenBothRequirementsAreSet_RequiresBoth()
    {
        var tooNew = Snapshot(accountAgeDays: 6, completedReviews: 3);
        var tooFewReviews = Snapshot(accountAgeDays: 8, completedReviews: 2);
        var eligible = Snapshot(accountAgeDays: 8, completedReviews: 3);

        Assert.Equal(
            KeyDropEligibilityOutcome.AccountTooNew,
            KeyDropEligibilityPolicy.Evaluate(tooNew));
        Assert.Equal(
            KeyDropEligibilityOutcome.NotEnoughCompletedReviews,
            KeyDropEligibilityPolicy.Evaluate(tooFewReviews));
        Assert.Equal(
            KeyDropEligibilityOutcome.Eligible,
            KeyDropEligibilityPolicy.Evaluate(eligible));
    }

    [Fact]
    public void Evaluate_WithNoRequirements_AllowsActiveAccount()
    {
        var snapshot = Snapshot(
            accountAgeDays: 0,
            completedReviews: 0,
            minimumAccountAgeDays: null,
            minimumCompletedReviewCount: null);

        Assert.Equal(
            KeyDropEligibilityOutcome.Eligible,
            KeyDropEligibilityPolicy.Evaluate(snapshot));
    }

    private static KeyDropEligibilitySnapshot Snapshot(
        int accountAgeDays,
        int completedReviews,
        int? minimumAccountAgeDays = 7,
        int? minimumCompletedReviewCount = 3) =>
        new(
            DropExists: true,
            AccountAvailable: true,
            MinimumAccountAgeDays: minimumAccountAgeDays,
            MinimumCompletedReviewCount: minimumCompletedReviewCount,
            AccountCreatedAt: Now.AddDays(-accountAgeDays),
            CompletedReviewCount: completedReviews,
            DatabaseNow: Now);
}
