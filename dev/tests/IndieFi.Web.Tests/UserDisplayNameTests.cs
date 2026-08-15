using IndieFi.Web.Modules.Accounts.Domain;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class UserDisplayNameTests
{
    [Fact]
    public void Resolve_WithNickname_ReturnsNickname()
    {
        Assert.Equal("Alpha Gamer", UserDisplayName.Resolve(1042, "Alpha Gamer"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_WithoutNickname_ReturnsStableFallback(string? nickname)
    {
        Assert.Equal("user#1042", UserDisplayName.Resolve(1042, nickname));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Resolve_WithInvalidUserId_Throws(long userId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => UserDisplayName.Resolve(userId, null));
    }
}
