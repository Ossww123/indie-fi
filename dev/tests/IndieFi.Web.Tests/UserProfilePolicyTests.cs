using IndieFi.Web.Modules.Profiles.Domain;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class UserProfilePolicyTests
{
    [Fact]
    public void TryCreateNickname_NormalizesCompatibilityCaseAndWhitespace()
    {
        var success = UserProfilePolicy.TryCreateNickname(
            "  Ａｌｐｈａ\t  게이머  ",
            out var nickname,
            out var error);

        Assert.True(success);
        Assert.NotNull(nickname);
        Assert.Equal("Alpha 게이머", nickname.Value);
        Assert.Equal("ALPHA 게이머", nickname.UniquenessKey);
        Assert.Equal(NicknameValidationError.None, error);
    }

    [Theory]
    [InlineData(null, NicknameValidationError.Required)]
    [InlineData(" ", NicknameValidationError.Required)]
    [InlineData("a", NicknameValidationError.TooShort)]
    [InlineData("nickname!", NicknameValidationError.InvalidCharacter)]
    [InlineData("玩家", NicknameValidationError.InvalidCharacter)]
    [InlineData("player🙂", NicknameValidationError.InvalidCharacter)]
    public void TryCreateNickname_WithInvalidValue_Rejects(
        string? input,
        NicknameValidationError expectedError)
    {
        var success = UserProfilePolicy.TryCreateNickname(
            input,
            out var nickname,
            out var error);

        Assert.False(success);
        Assert.Null(nickname);
        Assert.Equal(expectedError, error);
    }

    [Fact]
    public void TryCreateNickname_WhenLongerThanTwentyCharacters_Rejects()
    {
        var success = UserProfilePolicy.TryCreateNickname(
            new string('가', UserProfilePolicy.NicknameMaxLength + 1),
            out var nickname,
            out var error);

        Assert.False(success);
        Assert.Null(nickname);
        Assert.Equal(NicknameValidationError.TooLong, error);
    }

    [Fact]
    public void CanChangeNickname_AllowsFirstSettingAndExactSevenDayBoundary()
    {
        var now = new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);

        Assert.True(UserProfilePolicy.CanChangeNickname(null, now));
        Assert.False(UserProfilePolicy.CanChangeNickname(now, now.AddDays(7).AddTicks(-1)));
        Assert.True(UserProfilePolicy.CanChangeNickname(now, now.AddDays(7)));
    }

    [Fact]
    public void NormalizeBiography_TrimsAndNormalizesLineEndings()
    {
        var value = UserProfilePolicy.NormalizeBiography("  첫 줄\r\n둘째 줄\r  ");

        Assert.Equal("첫 줄\n둘째 줄", value);
    }
}
