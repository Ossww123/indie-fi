using IndieFi.Web.Modules.KeyDistributions.Domain;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class KeyDropStatusPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1, 1, false, 1, KeyDropStatus.Scheduled)]
    [InlineData(-1, 1, false, 1, KeyDropStatus.Active)]
    [InlineData(1, 0, false, 1, KeyDropStatus.SoldOut)]
    [InlineData(-1, 0, false, 1, KeyDropStatus.SoldOut)]
    [InlineData(1, 1, true, 1, KeyDropStatus.ClosedEarly)]
    [InlineData(-1, 0, true, 1, KeyDropStatus.ClosedEarly)]
    public void Resolve_ReturnsAuthoritativeStatus(
        int opensOffsetMinutes,
        int remainingKeys,
        bool isClosed,
        int totalKeys,
        KeyDropStatus expected)
    {
        var result = KeyDropStatusPolicy.Resolve(
            Now.AddMinutes(opensOffsetMinutes),
            totalKeys,
            remainingKeys,
            isClosed ? Now : null,
            Now);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Resolve_AtOpenBoundary_IsActive()
    {
        var result = KeyDropStatusPolicy.Resolve(Now, 1, 1, null, Now);

        Assert.Equal(KeyDropStatus.Active, result);
    }

    [Theory]
    [InlineData(KeyDropStatus.Scheduled, true, false, true)]
    [InlineData(KeyDropStatus.Active, true, true, true)]
    [InlineData(KeyDropStatus.SoldOut, false, false, false)]
    [InlineData(KeyDropStatus.ClosedEarly, false, false, false)]
    public void Capabilities_MatchStatusRules(
        KeyDropStatus status,
        bool canAddInventory,
        bool canClaim,
        bool canCloseEarly)
    {
        Assert.Equal(canAddInventory, KeyDropStatusPolicy.CanAddInventory(status));
        Assert.Equal(canClaim, KeyDropStatusPolicy.CanClaim(status));
        Assert.Equal(canCloseEarly, KeyDropStatusPolicy.CanCloseEarly(status));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 2)]
    public void Resolve_WithImpossibleInventory_Throws(
        int totalKeys,
        int remainingKeys)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            KeyDropStatusPolicy.Resolve(
                Now.AddMinutes(1),
                totalKeys,
                remainingKeys,
                null,
                Now));
    }
}
