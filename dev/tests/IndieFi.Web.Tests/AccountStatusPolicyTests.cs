using IndieFi.Web.Modules.Accounts.Domain;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class AccountStatusPolicyTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 14, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(AccountStatus.Active, null, AccountAccessState.Active)]
    [InlineData(
        AccountStatus.IndefinitelySuspended,
        null,
        AccountAccessState.Suspended)]
    public void Evaluate_WithStatusWithoutEnd_ReturnsExpectedState(
        AccountStatus status,
        DateTimeOffset? suspendedUntil,
        AccountAccessState expected)
    {
        Assert.Equal(expected, AccountStatusPolicy.Evaluate(status, suspendedUntil, Now));
    }

    [Fact]
    public void Evaluate_WithTemporarySuspensionBeforeEnd_ReturnsSuspended()
    {
        Assert.Equal(
            AccountAccessState.Suspended,
            AccountStatusPolicy.Evaluate(
                AccountStatus.TemporarilySuspended,
                Now.AddTicks(1),
                Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Evaluate_WithTemporarySuspensionAtOrAfterEnd_ReturnsExpired(
        int endOffsetTicks)
    {
        Assert.Equal(
            AccountAccessState.TemporarySuspensionExpired,
            AccountStatusPolicy.Evaluate(
                AccountStatus.TemporarilySuspended,
                Now.AddTicks(endOffsetTicks),
                Now));
    }

    [Theory]
    [InlineData(AccountStatus.Active, true)]
    [InlineData(AccountStatus.TemporarilySuspended, false)]
    [InlineData(AccountStatus.IndefinitelySuspended, true)]
    public void Evaluate_WithInvalidStatusShape_Throws(
        AccountStatus status,
        bool hasSuspensionEnd)
    {
        DateTimeOffset? suspendedUntil = hasSuspensionEnd
            ? Now.AddHours(1)
            : null;

        Assert.Throws<InvalidOperationException>(() =>
            AccountStatusPolicy.Evaluate(status, suspendedUntil, Now));
    }
}
