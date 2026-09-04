using System.Net;
using IndieFi.Web.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class GameFormPageTests
{
    private readonly PostgreSqlFixture _fixture;

    public GameFormPageTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Get_NewGame_ShowsLinkEditorAndItsPageModule()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);

        using var response = await client.GetAsync(
            "/Games/New",
            TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("data-play-links", html);
        Assert.Contains("data-max-links=\"5\"", html);
        Assert.Contains("name=\"Links[0].DisplayName\"", html);
        Assert.Contains("name=\"GenreCode\"", html);
        Assert.Contains("name=\"TagCodes\"", html);
        Assert.Contains("name=\"PlatformCodes\"", html);
        Assert.Contains("value=\"action\"", html);
        Assert.DoesNotContain("value=\"unspecified\"", html);
        Assert.Contains("/js/dist/games/play-links", html);

        using var moduleResponse = await client.GetAsync(
            "/js/dist/games/play-links.js",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, moduleResponse.StatusCode);
    }

    [Fact]
    public async Task Post_NewGame_WithLinks_StoresNormalizedOrderedCollection()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        var title = $"Linked game {Guid.NewGuid():N}";
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);
        var token = await GetAntiforgeryTokenAsync(client, "/Games/New");

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Title"] = title,
            ["Description"] = "Linked game description",
            ["GenreCode"] = "action",
            ["TagCodes"] = "single-player",
            ["PlatformCodes"] = "web",
            ["Links[0].DisplayName"] = "  Browser build  ",
            ["Links[0].Provider"] = "DeveloperWebsite",
            ["Links[0].TargetPlatform"] = "Web",
            ["Links[0].Url"] = "  https://example.com/browser  ",
            ["Links[1].DisplayName"] = "Steam",
            ["Links[1].Provider"] = "Steam",
            ["Links[1].TargetPlatform"] = string.Empty,
            ["Links[1].Url"] = "https://store.steampowered.com/app/123",
            ["__RequestVerificationToken"] = token,
        });
        using var response = await client.PostAsync(
            "/Games/New",
            form,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        await using var verificationDb = _fixture.NewDbContext();
        var game = await verificationDb.Games.SingleAsync(
            item => item.Title == title,
            TestContext.Current.CancellationToken);
        var links = await verificationDb.GamePlayLinks
            .Where(link => link.GameId == game.Id)
            .OrderBy(link => link.SortOrder)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, links.Count);
        Assert.Equal(10, game.GenreId);
        Assert.Equal(["Browser build", "Steam"], links.Select(link => link.DisplayName));
        Assert.Equal([0, 1], links.Select(link => link.SortOrder));
        Assert.Equal(GamePlayLinkPlatform.Web, links[0].TargetPlatform);
        Assert.Null(links[1].TargetPlatform);
        Assert.Equal(
            [10],
            await verificationDb.GameTagAssignments
                .Where(item => item.GameId == game.Id)
                .Select(item => item.TagId)
                .ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            [13],
            await verificationDb.GamePlatformAssignments
                .Where(item => item.GameId == game.Id)
                .Select(item => item.PlatformId)
                .ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Post_NewGame_WithInvalidTitleAndLink_ShowsBothErrorsAndCreatesNothing()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        var ownerId = await FindUserIdAsync(email);
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);
        var token = await GetAntiforgeryTokenAsync(client, "/Games/New");

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Title"] = "   ",
            ["Description"] = string.Empty,
            ["GenreCode"] = "action",
            ["PlatformCodes"] = "windows",
            ["Links[0].DisplayName"] = string.Empty,
            ["Links[0].Provider"] = "Other",
            ["Links[0].TargetPlatform"] = string.Empty,
            ["Links[0].Url"] = string.Empty,
            ["Links[1].DisplayName"] = "Local build",
            ["Links[1].Provider"] = "Other",
            ["Links[1].TargetPlatform"] = string.Empty,
            ["Links[1].Url"] = "http://127.0.0.1/play",
            ["__RequestVerificationToken"] = token,
        });
        using var response = await client.PostAsync(
            "/Games/New",
            form,
            TestContext.Current.CancellationToken);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("게임 제목을 입력해 주세요.", html);
        Assert.Contains("공개 인터넷 호스트의 외부 플레이 주소를 입력해 주세요.", html);
        Assert.Contains("data-valmsg-for=\"Links[0].Url\"", html);

        await using var verificationDb = _fixture.NewDbContext();
        Assert.False(await verificationDb.Games.AnyAsync(
            game => game.OwnerId == ownerId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Get_EditGame_LoadsExistingLinksInStoredOrder()
    {
        var owner = await CreateOwnerAndGameAsync();
        await AddLinksAsync(
            owner.GameId,
            ("First link", "https://example.com/first"),
            ("Second link", "https://example.com/second"));
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, owner.Email, owner.Password);

        using var response = await client.GetAsync(
            $"/Games/Edit/{owner.GameId}",
            TestContext.Current.CancellationToken);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("name=\"Links[0].DisplayName\"", html);
        Assert.Contains("name=\"Links[1].DisplayName\"", html);
        Assert.True(
            html.IndexOf("First link", StringComparison.Ordinal)
            < html.IndexOf("Second link", StringComparison.Ordinal));
        Assert.Contains("/js/dist/games/play-links", html);
    }

    [Fact]
    public async Task Post_EditGame_CanEditRemoveAndReorderLinksWithGameFields()
    {
        var owner = await CreateOwnerAndGameAsync();
        await AddLinksAsync(
            owner.GameId,
            ("First", "https://example.com/first"),
            ("Second", "https://example.com/second"),
            ("Remove", "https://example.com/remove"));
        var updatedTitle = $"Updated {Guid.NewGuid():N}";
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, owner.Email, owner.Password);
        var path = $"/Games/Edit/{owner.GameId}";
        var token = await GetAntiforgeryTokenAsync(client, path);

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Title"] = updatedTitle,
            ["Description"] = "Updated description",
            ["GenreCode"] = "role-playing",
            ["TagCodes"] = "pixel-art",
            ["PlatformCodes"] = "linux",
            ["Links[0].DisplayName"] = "Second updated",
            ["Links[0].Provider"] = "ItchIo",
            ["Links[0].TargetPlatform"] = "Multiple",
            ["Links[0].Url"] = "https://example.com/second-new",
            ["Links[1].DisplayName"] = "First",
            ["Links[1].Provider"] = "Other",
            ["Links[1].TargetPlatform"] = string.Empty,
            ["Links[1].Url"] = "https://example.com/first",
            ["__RequestVerificationToken"] = token,
        });
        using var response = await client.PostAsync(
            path,
            form,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        await using var verificationDb = _fixture.NewDbContext();
        var game = await verificationDb.Games.SingleAsync(
            item => item.Id == owner.GameId,
            TestContext.Current.CancellationToken);
        var links = await verificationDb.GamePlayLinks
            .Where(link => link.GameId == owner.GameId)
            .OrderBy(link => link.SortOrder)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(updatedTitle, game.Title);
        Assert.Equal("Updated description", game.Description);
        Assert.Equal(12, game.GenreId);
        Assert.Equal(["Second updated", "First"], links.Select(link => link.DisplayName));
        Assert.Equal([0, 1], links.Select(link => link.SortOrder));
        Assert.Equal(
            [28],
            await verificationDb.GameTagAssignments
                .Where(item => item.GameId == owner.GameId)
                .Select(item => item.TagId)
                .ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            [12],
            await verificationDb.GamePlatformAssignments
                .Where(item => item.GameId == owner.GameId)
                .Select(item => item.PlatformId)
                .ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Post_EditGame_WithInvalidLink_PreservesGameAndLinkCollection()
    {
        var owner = await CreateOwnerAndGameAsync();
        await AddLinksAsync(owner.GameId, ("Original", "https://example.com/original"));
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, owner.Email, owner.Password);
        var path = $"/Games/Edit/{owner.GameId}";
        var token = await GetAntiforgeryTokenAsync(client, path);

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Title"] = "Should not be stored",
            ["Description"] = "Should not be stored",
            ["GenreCode"] = "unspecified",
            ["PlatformCodes"] = "windows",
            ["Links[0].DisplayName"] = "Private address",
            ["Links[0].Provider"] = "Other",
            ["Links[0].TargetPlatform"] = "Web",
            ["Links[0].Url"] = "http://10.0.0.1/play",
            ["__RequestVerificationToken"] = token,
        });
        using var response = await client.PostAsync(
            path,
            form,
            TestContext.Current.CancellationToken);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("공개 인터넷 호스트의 외부 플레이 주소를 입력해 주세요.", html);

        await using var verificationDb = _fixture.NewDbContext();
        var game = await verificationDb.Games.SingleAsync(
            item => item.Id == owner.GameId,
            TestContext.Current.CancellationToken);
        var link = await verificationDb.GamePlayLinks.SingleAsync(
            item => item.GameId == owner.GameId,
            TestContext.Current.CancellationToken);
        Assert.Equal(owner.GameTitle, game.Title);
        Assert.Null(game.Description);
        Assert.Equal("Original", link.DisplayName);
        Assert.Equal("https://example.com/original", link.Url);
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return PageTestSupport.ExtractAntiforgeryToken(html);
    }

    private async Task<long> FindUserIdAsync(string email)
    {
        await using var db = _fixture.NewDbContext();
        return (await TestAccountProtection.FindUserAsync(
            db,
            email,
            TestContext.Current.CancellationToken)).Id;
    }

    private async Task<OwnedGame> CreateOwnerAndGameAsync()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var owner = await TestAccountProtection.FindUserAsync(
            db,
            email,
            TestContext.Current.CancellationToken);
        var game = await ReviewTestData.CreateGameAsync(db, owner.Id);
        return new OwnedGame(email, password, game.Id, game.Title);
    }

    private async Task AddLinksAsync(
        long gameId,
        params (string DisplayName, string Url)[] links)
    {
        await using var db = _fixture.NewDbContext();
        db.GamePlayLinks.AddRange(links.Select((link, sortOrder) => new GamePlayLink
        {
            GameId = gameId,
            DisplayName = link.DisplayName,
            Provider = GamePlayLinkProvider.Other,
            Url = link.Url,
            SortOrder = sortOrder,
        }));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private sealed record OwnedGame(
        string Email,
        string Password,
        long GameId,
        string GameTitle);
}
