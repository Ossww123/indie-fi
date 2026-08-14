using IndieFi.Web.Modules.Accounts.Domain;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class UserDisplayIdentifierTests
{
    [Fact]
    public void Format_WithPersistedUserId_ReturnsStablePublicForm()
    {
        Assert.Equal("user#1042", UserDisplayIdentifier.Format(1042));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Format_WithoutPersistedUserId_Throws(long userId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => UserDisplayIdentifier.Format(userId));
    }
}
