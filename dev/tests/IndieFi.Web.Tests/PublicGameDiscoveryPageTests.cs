using System.Net;
using System.Text.RegularExpressions;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Games.Domain;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed partial class PublicGameDiscoveryPageTests
{
    private readonly PostgreSqlFixture _fixture;

    public PublicGameDiscoveryPageTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Get_WithUrlFilters_RendersActiveSelectionsAndMatchingGames()
    {
        var token = $"ui{Guid.NewGuid():N}";
        await using (var db = _fixture.NewDbContext())
        {
            var owner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
            await AddGamesAsync(db, owner.Id, token, count: 1, genreId: 10);
            await AddGamesAsync(db, owner.Id, token, count: 1, genreId: 11);
        }

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        var path = "/?q=" + Uri.EscapeDataString(token)
            + "&genre=action&tags=cooperative&platforms=windows";

        using var response = await client.GetAsync(
            path,
            TestContext.Current.CancellationToken);
        var html = WebUtility.HtmlDecode(
            await response.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("data-discovery-filter", html);
        Assert.Contains("class=\"field-check\"", html);
        Assert.Contains($"value=\"{token}\"", html);
        Assert.Matches(SelectedGenrePattern(), html);
        Assert.Matches(SelectedTagPattern(), html);
        Assert.Matches(SelectedPlatformPattern(), html);
        Assert.DoesNotContain("value=\"unspecified\"", html);
        Assert.Equal(1, CountOccurrences(html, "data-discovery-result"));
        Assert.Contains($"{token} item 00", html);
    }

    [Fact]
    public async Task Get_WithInvalidQuery_ReturnsBadRequestAndVisibleValidationError()
    {
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/?q=x",
            TestContext.Current.CancellationToken);
        var html = WebUtility.HtmlDecode(
            await response.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("검색어는 공백 정리 후 2~100자여야 합니다.", html);
        Assert.Contains("data-discovery-validation", html);
        Assert.Contains("data-discovery-filter", html);
    }

    [Fact]
    public async Task Get_NextPageLink_PreservesFiltersAndUsesProtectedCursor()
    {
        var token = $"next{Guid.NewGuid():N}";
        await using (var db = _fixture.NewDbContext())
        {
            var owner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
            await AddGamesAsync(db, owner.Id, token, count: 25, genreId: 10);
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

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(24, CountOccurrences(firstHtml, "data-discovery-result"));
        var nextHref = NextLinkPattern().Match(firstHtml).Groups[1].Value;
        Assert.NotEmpty(nextHref);
        var nextUri = new Uri(client.BaseAddress!, nextHref);
        var nextQuery = QueryHelpers.ParseQuery(nextUri.Query);
        Assert.Equal(token, nextQuery["q"]);
        Assert.Equal("action", nextQuery["genre"]);
        Assert.Equal("cooperative", nextQuery["tags"]);
        Assert.Equal("windows", nextQuery["platforms"]);
        Assert.NotEmpty(nextQuery["cursor"].ToString());

        using var secondResponse = await client.GetAsync(
            nextUri,
            TestContext.Current.CancellationToken);
        var secondHtml = WebUtility.HtmlDecode(
            await secondResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(1, CountOccurrences(secondHtml, "data-discovery-result"));
        Assert.DoesNotContain("data-discovery-next", secondHtml);
    }

    private static async Task AddGamesAsync(
        IndieFi.Web.Data.AppDbContext db,
        long ownerId,
        string token,
        int count,
        long genreId)
    {
        var games = Enumerable.Range(0, count)
            .Select(index => new Game
            {
                OwnerId = ownerId,
                GenreId = genreId,
                Title = $"{token} item {index:D2}",
                Description = "검색 화면 테스트 소개",
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

    [GeneratedRegex("<option[^>]*value=\"action\"[^>]*selected[^>]*>")]
    private static partial Regex SelectedGenrePattern();

    [GeneratedRegex("<input(?=[^>]*name=\"tags\")(?=[^>]*value=\"cooperative\")(?=[^>]*checked)[^>]*>")]
    private static partial Regex SelectedTagPattern();

    [GeneratedRegex("<input(?=[^>]*name=\"platforms\")(?=[^>]*value=\"windows\")(?=[^>]*checked)[^>]*>")]
    private static partial Regex SelectedPlatformPattern();

    [GeneratedRegex("<a[^>]*data-discovery-next[^>]*href=\"([^\"]+)\"")]
    private static partial Regex NextLinkPattern();
}
