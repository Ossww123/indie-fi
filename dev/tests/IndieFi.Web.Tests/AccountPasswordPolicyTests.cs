using IndieFi.Web.Modules.Accounts.Domain;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class AccountPasswordPolicyTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(32)]
    [InlineData(128)]
    public void TryValidate_WithSupportedLength_ReturnsValid(int length)
    {
        var accepted = AccountPasswordPolicy.TryValidate(
            new string('p', length),
            out var error);

        Assert.True(accepted);
        Assert.Equal(AccountPasswordError.None, error);
    }

    [Theory]
    [InlineData(null, AccountPasswordError.Required)]
    [InlineData("", AccountPasswordError.Required)]
    [InlineData("1234567", AccountPasswordError.TooShort)]
    public void TryValidate_WithMissingOrShortPassword_ReturnsExpectedError(
        string? password,
        AccountPasswordError expectedError)
    {
        var accepted = AccountPasswordPolicy.TryValidate(password, out var error);

        Assert.False(accepted);
        Assert.Equal(expectedError, error);
    }

    [Fact]
    public void TryValidate_WhenPasswordExceeds128Characters_ReturnsTooLong()
    {
        var accepted = AccountPasswordPolicy.TryValidate(
            new string('p', 129),
            out var error);

        Assert.False(accepted);
        Assert.Equal(AccountPasswordError.TooLong, error);
    }
}
