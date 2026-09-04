using System.Net;
using System.Text.RegularExpressions;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Games.Domain;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed partial class PublicGameDiscoveryIntegrationTests
{
    private readonly PostgreSqlFixture _fixture;

    public PublicGameDiscoveryIntegrationTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Search_KoreanCaseAndLikeMetacharacters_AreLiteralPartialMatches()
    {
        var token = $"한글star{Guid.NewGuid():N}";
        var expectedTitle = $"{token.ToUpperInvariant()} 모험";
        await using (var db = _fixture.NewDbContext())
        {
            var owner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
            db.Games.AddRange(
                new Game
                {
                    OwnerId = owner.Id,
                    GenreId = 10,
                    Title = expectedTitle,
                    Description = "리터럴 100%_\\ 탐험",
                },
                new Game
                {
                    OwnerId = owner.Id,
                    GenreId = 10,
                    Title = $"{token} wildcard decoy",
                    Description = "리터럴 100ABCD 탐험",
                });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        var query = Uri.EscapeDataString($"{token} 100%_\\");

        using var response = await client.GetAsync(
            $"/?q={query}",
            TestContext.Current.CancellationToken);
        var html = WebUtility.HtmlDecode(
            await response.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, CountOccurrences(html, "data-discovery-result"));
        Assert.Contains(expectedTitle, html);
        Assert.DoesNotContain("wildcard decoy", html);
    }

    [Fact]
    public async Task Search_UnknownAndInactiveCatalogCodes_ReturnsBadRequest()
    {
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/?genre=unspecified&tags=unknown-tag&platforms=unspecified",
            TestContext.Current.CancellationToken);
        var html = WebUtility.HtmlDecode(
            await response.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("활성 상태인 장르 코드만 선택할 수 있습니다.", html);
        Assert.Contains("알 수 없거나 비활성인 태그 코드가 포함되어 있습니다.", html);
        Assert.Contains("알 수 없거나 비활성인 지원 플랫폼 코드가 포함되어 있습니다.", html);
    }

    [Fact]
    public async Task Search_DamagedOrDifferentFilterCursor_ReturnsBadRequest()
    {
        var token = $"cursor{Guid.NewGuid():N}";
        await using (var db = _fixture.NewDbContext())
        {
            var owner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
            var games = Enumerable.Range(0, 25)
                .Select(index => new Game
                {
                    OwnerId = owner.Id,
                    GenreId = 10,
                    Title = $"{token} item {index:D2}",
                })
                .ToArray();
            db.Games.AddRange(games);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            db.GameTagAssignments.AddRange(games.Select(game =>
                new GameTagAssignment { GameId = game.Id, TagId = 14 }));
            db.GamePlatformAssignments.AddRange(games.Select(game =>
                new GamePlatformAssignment { GameId = game.Id, PlatformId = 10 }));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        var firstPath = "/?q=" + Uri.EscapeDataString(token)
            + "&genre=action&tags=cooperative&platforms=windows";
        using var firstResponse = await client.GetAsync(
            firstPath,
            TestContext.Current.CancellationToken);
        var firstHtml = WebUtility.HtmlDecode(
            await firstResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
        var nextHref = NextLinkPattern().Match(firstHtml).Groups[1].Value;
        var nextUri = new Uri(client.BaseAddress!, nextHref);
        var nextQuery = QueryHelpers.ParseQuery(nextUri.Query);
        var cursor = nextQuery["cursor"].ToString();
        Assert.NotEmpty(cursor);

        var middle = cursor.Length / 2;
        var damagedCursor = cursor[..middle]
            + (cursor[middle] == 'A' ? "B" : "A")
            + cursor[(middle + 1)..];
        var common = "&genre=action&tags=cooperative&platforms=windows&cursor=";
        using var damagedResponse = await client.GetAsync(
            "/?q=" + Uri.EscapeDataString(token)
                + common + Uri.EscapeDataString(damagedCursor),
            TestContext.Current.CancellationToken);
        var damagedHtml = WebUtility.HtmlDecode(
            await damagedResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.BadRequest, damagedResponse.StatusCode);
        Assert.Contains("검색 페이지 커서가 올바르지 않습니다.", damagedHtml);

        using var mismatchResponse = await client.GetAsync(
            "/?q=" + Uri.EscapeDataString(token + "changed")
                + common + Uri.EscapeDataString(cursor),
            TestContext.Current.CancellationToken);
        var mismatchHtml = WebUtility.HtmlDecode(
            await mismatchResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.BadRequest, mismatchResponse.StatusCode);
        Assert.Contains("현재 검색 조건과 일치하지 않는 페이지 커서입니다.", mismatchHtml);
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var position = 0;
        while ((position = source.IndexOf(
                   value,
                   position,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            position += value.Length;
        }

        return count;
    }

    [GeneratedRegex("<a[^>]*data-discovery-next[^>]*href=\"([^\"]+)\"")]
    private static partial Regex NextLinkPattern();
}
