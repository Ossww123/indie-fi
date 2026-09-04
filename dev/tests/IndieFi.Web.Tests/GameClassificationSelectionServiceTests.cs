using IndieFi.Web.Modules.Games.Application;
using IndieFi.Web.Modules.Games.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class GameClassificationSelectionServiceTests
{
    private readonly PostgreSqlFixture _fixture;

    public GameClassificationSelectionServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ValidateForCreateAsync_ResolvesActiveCodesInCatalogOrder()
    {
        await using var db = _fixture.NewDbContext();
        var result = await new GameClassificationSelectionService(db)
            .ValidateForCreateAsync(
                new GameClassificationInput(
                    "role-playing",
                    ["pixel-art", "single-player"],
                    ["linux", "windows"]),
                TestContext.Current.CancellationToken);

        Assert.True(result.IsValid);
        Assert.Empty(result.Violations);
        Assert.NotNull(result.Selection);
        Assert.Equal(12, result.Selection.GenreId);
        Assert.Equal([10, 28], result.Selection.TagIds);
        Assert.Equal([10, 12], result.Selection.PlatformIds);
    }

    [Fact]
    public async Task ValidateForCreateAsync_RequiresGenreAndPlatform()
    {
        await using var db = _fixture.NewDbContext();
        var result = await new GameClassificationSelectionService(db)
            .ValidateForCreateAsync(
                new GameClassificationInput(null, null, null),
                TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Null(result.Selection);
        Assert.Equal(
            [
                GameClassificationSelectionService.GenreField,
                GameClassificationSelectionService.PlatformsField,
            ],
            result.Violations.Select(item => item.Field));
    }

    [Fact]
    public async Task ValidateForCreateAsync_RejectsDuplicatesAndUnavailableCodes()
    {
        await using var db = _fixture.NewDbContext();
        var result = await new GameClassificationSelectionService(db)
            .ValidateForCreateAsync(
                new GameClassificationInput(
                    "unspecified",
                    ["single-player", "single-player"],
                    ["unspecified"]),
                TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Null(result.Selection);
        Assert.Contains(result.Violations, item =>
            item.Field == GameClassificationSelectionService.GenreField
            && item.Message == "선택할 수 없는 장르입니다.");
        Assert.Contains(result.Violations, item =>
            item.Field == GameClassificationSelectionService.TagsField
            && item.Message == "같은 태그를 중복해서 선택할 수 없습니다.");
        Assert.Contains(result.Violations, item =>
            item.Field == GameClassificationSelectionService.PlatformsField
            && item.Message == "선택할 수 없는 지원 플랫폼이 포함되어 있습니다.");
    }

    [Fact]
    public async Task ValidateForCreateAsync_RejectsCollectionsOverTheirLimits()
    {
        var tagCodes = Enumerable.Range(10, 9)
            .Select(id => id switch
            {
                10 => "single-player",
                11 => "multiplayer",
                12 => "online-multiplayer",
                13 => "local-multiplayer",
                14 => "cooperative",
                15 => "pvp",
                16 => "story-rich",
                17 => "exploration",
                _ => "turn-based",
            })
            .ToArray();
        var platformCodes = new[]
        {
            "windows",
            "macos",
            "linux",
            "web",
            "android",
            "ios",
            "steam-deck",
            "playstation-5",
            "xbox-series",
            "nintendo-switch",
            "virtual-reality",
            "other",
            "unknown-platform",
        };

        await using var db = _fixture.NewDbContext();
        var result = await new GameClassificationSelectionService(db)
            .ValidateForCreateAsync(
                new GameClassificationInput("action", tagCodes, platformCodes),
                TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, item =>
            item.Field == GameClassificationSelectionService.TagsField
            && item.Message == "태그는 최대 8개까지 선택할 수 있습니다.");
        Assert.Contains(result.Violations, item =>
            item.Field == GameClassificationSelectionService.PlatformsField
            && item.Message == "지원 플랫폼은 최대 12개까지 선택할 수 있습니다.");
    }

    [Fact]
    public async Task ValidateForUpdateAsync_AllowsOnlyCurrentInactiveSelections()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = _fixture.NewOwnerDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var currentTag = await db.GameTags.SingleAsync(
            item => item.Id == 10,
            cancellationToken);
        currentTag.IsActive = false;
        db.GameTags.Add(new GameTag
        {
            Id = -5001,
            Code = "inactive-not-selected",
            DisplayName = "현재 선택하지 않은 비활성 태그",
            SortOrder = 5,
            IsActive = false,
        });
        await db.SaveChangesAsync(cancellationToken);

        var service = new GameClassificationSelectionService(db);
        var current = new CurrentGameClassification(
            GenreId: 1,
            TagIds: [10],
            PlatformIds: [1]);
        var preserved = await service.ValidateForUpdateAsync(
            new GameClassificationInput(
                "unspecified",
                ["single-player"],
                ["unspecified"]),
            current,
            cancellationToken);
        var unselectedInactive = await service.ValidateForUpdateAsync(
            new GameClassificationInput(
                "action",
                ["inactive-not-selected"],
                ["windows"]),
            current,
            cancellationToken);
        var replaced = await service.ValidateForUpdateAsync(
            new GameClassificationInput(
                "action",
                [],
                ["windows"]),
            current,
            cancellationToken);

        Assert.NotNull(preserved.Selection);
        Assert.Equal(1, preserved.Selection.GenreId);
        Assert.Equal([10], preserved.Selection.TagIds);
        Assert.Equal([1], preserved.Selection.PlatformIds);
        Assert.False(unselectedInactive.IsValid);
        Assert.Contains(unselectedInactive.Violations, item =>
            item.Field == GameClassificationSelectionService.TagsField);
        Assert.NotNull(replaced.Selection);
        Assert.Equal(10, replaced.Selection.GenreId);
        Assert.Empty(replaced.Selection.TagIds);
        Assert.Equal([10], replaced.Selection.PlatformIds);

        await transaction.RollbackAsync(cancellationToken);
    }
}
