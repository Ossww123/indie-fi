using IndieFi.Web.Modules.Accounts.Domain;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class AccountEmailAddressPolicyTests
{
    [Theory]
    [InlineData("person@example.com")]
    [InlineData("First.Last+game@example.co.kr")]
    [InlineData("user_name@example.test")]
    [InlineData("user!#$%&'*+-/=?^_`{|}~@example.com")]
    public void TryCreate_WithSupportedAddress_ReturnsDisplayAndLookupValues(string input)
    {
        var accepted = AccountEmailAddressPolicy.TryCreate(
            $"  {input}  ",
            out var address,
            out var error);

        Assert.True(accepted);
        Assert.NotNull(address);
        Assert.Equal(input, address.DisplayValue);
        Assert.Equal(input.ToUpperInvariant(), address.NormalizedValue);
        Assert.Equal(AccountEmailAddressError.None, error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("asbas142@asda")]
    [InlineData("user@@example.com")]
    [InlineData(".user@example.com")]
    [InlineData("user.@example.com")]
    [InlineData("first..last@example.com")]
    [InlineData("user@example..com")]
    [InlineData("user@-example.com")]
    [InlineData("user@example-.com")]
    [InlineData("user@한글.com")]
    [InlineData("사용자@example.com")]
    [InlineData("user name@example.com")]
    public void TryCreate_WithUnsupportedAddress_ReturnsInvalid(
        string? input)
    {
        var accepted = AccountEmailAddressPolicy.TryCreate(
            input,
            out var address,
            out var error);

        Assert.False(accepted);
        Assert.Null(address);
        Assert.NotEqual(AccountEmailAddressError.None, error);
    }

    [Fact]
    public void TryCreate_WhenLocalPartExceeds64Characters_ReturnsInvalidFormat()
    {
        var input = $"{new string('a', 65)}@example.com";

        var accepted = AccountEmailAddressPolicy.TryCreate(
            input,
            out var address,
            out var error);

        Assert.False(accepted);
        Assert.Null(address);
        Assert.Equal(AccountEmailAddressError.InvalidFormat, error);
    }

    [Fact]
    public void TryCreate_WhenAddressExceeds254Characters_ReturnsTooLong()
    {
        var input = $"user@{new string('a', 246)}.com";

        var accepted = AccountEmailAddressPolicy.TryCreate(
            input,
            out var address,
            out var error);

        Assert.False(accepted);
        Assert.Null(address);
        Assert.Equal(AccountEmailAddressError.TooLong, error);
    }
}
