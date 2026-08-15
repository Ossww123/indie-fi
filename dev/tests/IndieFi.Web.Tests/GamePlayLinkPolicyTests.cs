using IndieFi.Web.Modules.Games.Domain;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class GamePlayLinkPolicyTests
{
    [Theory]
    [InlineData("https://store.steampowered.com/app/123/game")]
    [InlineData("http://example.com/download?build=windows#latest")]
    [InlineData("https://게임.example/play")]
    [InlineData("https://8.8.8.8/play")]
    [InlineData("https://[2606:4700:4700::1111]/play")]
    public void TryCreateAddress_WithPublicHttpAddress_NormalizesValue(string input)
    {
        var success = GamePlayLinkPolicy.TryCreateAddress(
            $"  {input}  ",
            out var address,
            out var error);

        Assert.True(success);
        Assert.NotNull(address);
        Assert.Equal(GamePlayLinkAddressError.None, error);
        Assert.StartsWith("http", address.Value, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("javascript:alert(1)", GamePlayLinkAddressError.UnsupportedScheme)]
    [InlineData("file:///tmp/game.zip", GamePlayLinkAddressError.UnsupportedScheme)]
    [InlineData("ftp://example.com/game", GamePlayLinkAddressError.UnsupportedScheme)]
    [InlineData("https://user:password@example.com/game", GamePlayLinkAddressError.UserInfoNotAllowed)]
    [InlineData("https://localhost/game", GamePlayLinkAddressError.NonPublicHost)]
    [InlineData("https://devbox.local/game", GamePlayLinkAddressError.NonPublicHost)]
    [InlineData("https://printer/game", GamePlayLinkAddressError.NonPublicHost)]
    [InlineData("https://127.0.0.1/game", GamePlayLinkAddressError.NonPublicHost)]
    [InlineData("https://10.0.0.1/game", GamePlayLinkAddressError.NonPublicHost)]
    [InlineData("https://169.254.10.20/game", GamePlayLinkAddressError.NonPublicHost)]
    [InlineData("https://192.168.0.1/game", GamePlayLinkAddressError.NonPublicHost)]
    [InlineData("https://[::1]/game", GamePlayLinkAddressError.NonPublicHost)]
    [InlineData("https://[fc00::1]/game", GamePlayLinkAddressError.NonPublicHost)]
    [InlineData("https://[fe80::1]/game", GamePlayLinkAddressError.NonPublicHost)]
    public void TryCreateAddress_WithUnsafeAddress_Rejects(
        string input,
        GamePlayLinkAddressError expectedError)
    {
        var success = GamePlayLinkPolicy.TryCreateAddress(
            input,
            out var address,
            out var error);

        Assert.False(success);
        Assert.Null(address);
        Assert.Equal(expectedError, error);
    }

    [Fact]
    public void TryCreateAddress_WhenUrlExceedsLimit_RejectsBeforeParsing()
    {
        var input = $"https://example.com/{new string('a', GamePlayLinkPolicy.UrlMaxLength)}";

        var success = GamePlayLinkPolicy.TryCreateAddress(
            input,
            out var address,
            out var error);

        Assert.False(success);
        Assert.Null(address);
        Assert.Equal(GamePlayLinkAddressError.TooLong, error);
    }

    [Fact]
    public void TryNormalizeDisplayName_TrimsValidValue()
    {
        var success = GamePlayLinkPolicy.TryNormalizeDisplayName(
            "  Play on itch.io  ",
            out var displayName,
            out var error);

        Assert.True(success);
        Assert.Equal("Play on itch.io", displayName);
        Assert.Equal(GamePlayLinkDisplayNameError.None, error);
    }

    [Theory]
    [InlineData(null, GamePlayLinkDisplayNameError.Required)]
    [InlineData("   ", GamePlayLinkDisplayNameError.Required)]
    public void TryNormalizeDisplayName_WhenBlank_Rejects(
        string? input,
        GamePlayLinkDisplayNameError expectedError)
    {
        var success = GamePlayLinkPolicy.TryNormalizeDisplayName(
            input,
            out var displayName,
            out var error);

        Assert.False(success);
        Assert.Null(displayName);
        Assert.Equal(expectedError, error);
    }

    [Fact]
    public void TryNormalizeDisplayName_WhenValueExceedsLimit_Rejects()
    {
        var success = GamePlayLinkPolicy.TryNormalizeDisplayName(
            new string('n', GamePlayLinkPolicy.DisplayNameMaxLength + 1),
            out var displayName,
            out var error);

        Assert.False(success);
        Assert.Null(displayName);
        Assert.Equal(GamePlayLinkDisplayNameError.TooLong, error);
    }
}
