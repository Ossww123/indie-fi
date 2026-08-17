using IndieFi.Web.Modules.Games.Application;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class PublicGameDiscoveryInputValidatorTests
{
    [Fact]
    public void Validate_BlankInput_CreatesUnfilteredRequest()
    {
        var result = PublicGameDiscoveryInputValidator.Validate(new(
            Query: " \t\r\n ",
            GenreCode: null,
            TagCodes: null,
            PlatformCodes: null,
            CreatedFrom: null,
            CreatedBefore: null));

        Assert.True(result.IsValid);
        Assert.Empty(result.Violations);
        Assert.NotNull(result.Filter);
        Assert.Null(result.Filter.Query);
        Assert.Empty(result.Filter.SearchTokens);
        Assert.Null(result.Filter.GenreCode);
        Assert.Empty(result.Filter.TagCodes);
        Assert.Empty(result.Filter.PlatformCodes);
        Assert.Null(result.Filter.CreatedFromUtc);
        Assert.Null(result.Filter.CreatedBeforeUtc);
    }

    [Fact]
    public void Validate_Query_CollapsesUnicodeWhitespaceAndKeepsLiteralCharacters()
    {
        var result = Validate(query: "  액션\tRPG\n100%_\\  ");

        Assert.True(result.IsValid);
        Assert.Equal("액션 RPG 100%_\\", result.Filter!.Query);
        Assert.Equal(["액션", "RPG", "100%_\\"], result.Filter.SearchTokens);
    }

    [Theory]
    [InlineData("가")]
    [InlineData("a")]
    public void Validate_OneCharacterQuery_Rejects(string query)
    {
        var result = Validate(query: query);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Violations,
            violation => violation.Field == PublicGameDiscoveryInputValidator.QueryField);
    }

    [Fact]
    public void Validate_QueryOverOneHundredRunes_Rejects()
    {
        var result = Validate(query: string.Concat(Enumerable.Repeat("🎮", 101)));

        Assert.False(result.IsValid);
        Assert.Contains("100자", Assert.Single(result.Violations).Message);
    }

    [Fact]
    public void Validate_MoreThanEightTokens_Rejects()
    {
        var result = Validate(query: "aa bb cc dd ee ff gg hh ii");

        Assert.False(result.IsValid);
        Assert.Contains("8개", Assert.Single(result.Violations).Message);
    }

    [Fact]
    public void Validate_ClassificationCodes_DeduplicatesInInputOrder()
    {
        var result = PublicGameDiscoveryInputValidator.Validate(new(
            Query: null,
            GenreCode: "action",
            TagCodes: ["co-op", "pixel-art", "co-op"],
            PlatformCodes: ["windows", "linux", "windows"],
            CreatedFrom: null,
            CreatedBefore: null));

        Assert.True(result.IsValid);
        Assert.Equal("action", result.Filter!.GenreCode);
        Assert.Equal(["co-op", "pixel-art"], result.Filter.TagCodes);
        Assert.Equal(["windows", "linux"], result.Filter.PlatformCodes);
    }

    [Fact]
    public void Validate_InvalidAndExcessClassificationCodes_ReturnsEveryViolation()
    {
        var result = PublicGameDiscoveryInputValidator.Validate(new(
            Query: null,
            GenreCode: "Action",
            TagCodes: Enumerable.Range(0, 9).Select(index => $"tag-{index}").ToArray(),
            PlatformCodes: ["windows", " bad "],
            CreatedFrom: null,
            CreatedBefore: null));

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Violations,
            violation => violation.Field == PublicGameDiscoveryInputValidator.GenreField);
        Assert.Contains(
            result.Violations,
            violation => violation.Field == PublicGameDiscoveryInputValidator.TagsField
                && violation.Message.Contains("8개", StringComparison.Ordinal));
        Assert.Contains(
            result.Violations,
            violation => violation.Field == PublicGameDiscoveryInputValidator.PlatformsField);
    }

    [Fact]
    public void Validate_Rfc3339Range_NormalizesOffsetsToUtc()
    {
        var result = PublicGameDiscoveryInputValidator.Validate(new(
            Query: null,
            GenreCode: null,
            TagCodes: null,
            PlatformCodes: null,
            CreatedFrom: "2026-08-01T09:30:00.123+09:00",
            CreatedBefore: "2026-08-02T00:00:00Z"));

        Assert.True(result.IsValid);
        Assert.Equal(
            new DateTimeOffset(2026, 8, 1, 0, 30, 0, 123, TimeSpan.Zero),
            result.Filter!.CreatedFromUtc);
        Assert.Equal(
            new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero),
            result.Filter.CreatedBeforeUtc);
    }

    [Theory]
    [InlineData("2026-08-01T09:30:00")]
    [InlineData("2026-08-01 09:30:00+09:00")]
    [InlineData("2026-08-01T09:30:00+0900")]
    [InlineData(" 2026-08-01T09:30:00Z")]
    public void Validate_NonRfc3339OrOffsetlessTime_Rejects(string createdFrom)
    {
        var result = PublicGameDiscoveryInputValidator.Validate(new(
            Query: null,
            GenreCode: null,
            TagCodes: null,
            PlatformCodes: null,
            CreatedFrom: createdFrom,
            CreatedBefore: null));

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Violations,
            violation => violation.Field == PublicGameDiscoveryInputValidator.CreatedFromField);
    }

    [Theory]
    [InlineData("2026-08-02T00:00:00Z", "2026-08-01T00:00:00Z")]
    [InlineData("2026-08-01T00:00:00Z", "2026-08-01T00:00:00Z")]
    public void Validate_EmptyOrReversedTimeRange_Rejects(
        string createdFrom,
        string createdBefore)
    {
        var result = PublicGameDiscoveryInputValidator.Validate(new(
            Query: null,
            GenreCode: null,
            TagCodes: null,
            PlatformCodes: null,
            CreatedFrom: createdFrom,
            CreatedBefore: createdBefore));

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Violations,
            violation => violation.Field == PublicGameDiscoveryInputValidator.CreatedBeforeField);
    }

    private static PublicGameDiscoveryValidationResult Validate(string? query) =>
        PublicGameDiscoveryInputValidator.Validate(new(
            Query: query,
            GenreCode: null,
            TagCodes: null,
            PlatformCodes: null,
            CreatedFrom: null,
            CreatedBefore: null));
}
