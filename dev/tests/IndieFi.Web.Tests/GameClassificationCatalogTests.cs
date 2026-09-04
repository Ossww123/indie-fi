using IndieFi.Web.Modules.Games.Domain;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class GameClassificationCatalogTests
{
    [Fact]
    public void InitialCatalogs_MatchApprovedSelectableCodes()
    {
        Assert.Equal(
            [
                "action", "adventure", "role-playing", "strategy", "simulation",
                "puzzle", "platformer", "shooter", "racing", "sports", "fighting",
                "rhythm", "horror", "visual-novel", "roguelike", "survival",
                "sandbox", "card-board", "casual", "other",
            ],
            SelectableCodes(GameClassificationCatalog.Genres));

        Assert.Equal(
            [
                "single-player", "multiplayer", "online-multiplayer", "local-multiplayer",
                "cooperative", "pvp", "story-rich", "exploration", "turn-based",
                "real-time", "procedural-generation", "replayable", "difficult",
                "relaxing", "short", "open-world", "2d", "3d", "pixel-art",
                "hand-drawn", "retro", "atmospheric", "controller-support",
                "keyboard-mouse", "touch-controls", "family-friendly",
                "accessibility-options", "experimental",
            ],
            SelectableCodes(GameClassificationCatalog.Tags));

        Assert.Equal(
            [
                "windows", "macos", "linux", "web", "android", "ios", "steam-deck",
                "playstation-5", "xbox-series", "nintendo-switch", "virtual-reality",
                "other",
            ],
            SelectableCodes(GameClassificationCatalog.Platforms));
    }

    [Fact]
    public void InitialCatalogs_HaveValidUniqueStableDefinitions()
    {
        AssertValidCatalog(GameClassificationCatalog.Genres);
        AssertValidCatalog(GameClassificationCatalog.Tags);
        AssertValidCatalog(GameClassificationCatalog.Platforms);
    }

    [Fact]
    public void UnspecifiedDefinitions_AreInactiveAndReservedForLegacyGames()
    {
        var genre = Assert.Single(
            GameClassificationCatalog.Genres,
            item => item.Code == GameClassificationCatalog.UnspecifiedCode);
        var platform = Assert.Single(
            GameClassificationCatalog.Platforms,
            item => item.Code == GameClassificationCatalog.UnspecifiedCode);

        Assert.Equal(GameClassificationCatalog.UnspecifiedGenreId, genre.Id);
        Assert.Equal(GameClassificationCatalog.UnspecifiedPlatformId, platform.Id);
        Assert.False(genre.IsActive);
        Assert.False(platform.IsActive);
        Assert.DoesNotContain(
            GameClassificationCatalog.Tags,
            item => item.Code == GameClassificationCatalog.UnspecifiedCode);
    }

    [Fact]
    public void SelectionLimits_MatchApprovedPolicy()
    {
        Assert.Equal(8, GameClassificationPolicy.MaxTagsPerGame);
        Assert.Equal(12, GameClassificationPolicy.MaxPlatformsPerGame);
        Assert.Equal(
            GameClassificationPolicy.MaxPlatformsPerGame,
            GameClassificationCatalog.Platforms.Count(item => item.IsActive));
    }

    [Theory]
    [InlineData("valid-code")]
    [InlineData("2d")]
    [InlineData("xbox-series")]
    public void IsValidCode_WithStableCode_Accepts(string code)
    {
        Assert.True(GameClassificationPolicy.IsValidCode(code));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Uppercase")]
    [InlineData("has space")]
    [InlineData("has_underscore")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("double--hyphen")]
    [InlineData("한글")]
    public void IsValidCode_WithUnstableCode_Rejects(string? code)
    {
        Assert.False(GameClassificationPolicy.IsValidCode(code));
    }

    private static string[] SelectableCodes(
        IReadOnlyList<GameClassificationDefinition> definitions)
    {
        return definitions
            .Where(item => item.IsActive)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .Select(item => item.Code)
            .ToArray();
    }

    private static void AssertValidCatalog(
        IReadOnlyList<GameClassificationDefinition> definitions)
    {
        Assert.NotEmpty(definitions);
        Assert.Equal(definitions.Count, definitions.Select(item => item.Id).Distinct().Count());
        Assert.Equal(
            definitions.Count,
            definitions.Select(item => item.Code).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            definitions.Count,
            definitions.Select(item => item.DisplayName).Distinct(StringComparer.Ordinal).Count());

        foreach (var definition in definitions)
        {
            Assert.True(definition.Id > 0);
            Assert.True(GameClassificationPolicy.IsValidCode(definition.Code));
            Assert.True(GameClassificationPolicy.IsValidDisplayName(definition.DisplayName));
            Assert.True(GameClassificationPolicy.IsValidSortOrder(definition.SortOrder));
        }

        var expectedOrder = definitions
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Code, StringComparer.Ordinal);
        Assert.Equal(expectedOrder, definitions);
    }
}
