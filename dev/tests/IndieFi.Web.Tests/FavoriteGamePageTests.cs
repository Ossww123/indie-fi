using System.Net;
using IndieFi.Web.Modules.Profiles.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class FavoriteGamePageTests
{
    private readonly PostgreSqlFixture _fixture;

    public FavoriteGamePageTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GameDetail_AnonymousUserSeesLoginLinkAndCannotFavorite()
    {
        var data = await CreateUserAndGameAsync();
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();

        using var detailResponse = await client.GetAsync(
            $"/Games/Detail/{data.GameId}",
            TestContext.Current.CancellationToken);
        var detailHtml = WebUtility.HtmlDecode(
            await detailResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        Assert.Contains("로그인 후 관심 게임 추가", detailHtml);
        Assert.Contains(
            $"ReturnUrl=%2FGames%2FDetail%2F{data.GameId}",
            detailHtml,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("handler=Favorite", detailHtml);

        using var loginResponse = await client.GetAsync(
            "/Login",
            TestContext.Current.CancellationToken);
        var loginHtml = await loginResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        using var favoriteResponse = await PostFavoriteAsync(
            client,
            data.GameId,
            "Favorite",
            PageTestSupport.ExtractAntiforgeryToken(loginHtml));

        Assert.Equal(HttpStatusCode.Redirect, favoriteResponse.StatusCode);
        Assert.StartsWith(
            "/Login?ReturnUrl=",
            favoriteResponse.Headers.Location?.PathAndQuery);
        await AssertFavoriteCountAsync(data.UserId, data.GameId, 0);
    }

    [Fact]
    public async Task FavoriteGames_AnonymousUserIsRedirectedToLogin()
    {
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/Account/FavoriteGames",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(
            "/Login?ReturnUrl=%2FAccount%2FFavoriteGames",
            response.Headers.Location?.PathAndQuery);
    }

    [Fact]
    public async Task FavoriteAndUnfavorite_RepeatedRequestsConvergeAndUpdatePrivateList()
    {
        var data = await CreateUserAndGameAsync(addOwnerProfile: true);
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, data.Email, data.Password);

        using var initialResponse = await client.GetAsync(
            $"/Games/Detail/{data.GameId}",
            TestContext.Current.CancellationToken);
        var initialHtml = WebUtility.HtmlDecode(
            await initialResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
        var initialToken = PageTestSupport.ExtractAntiforgeryToken(initialHtml);
        Assert.Contains(">관심 게임 추가</button>", initialHtml);
        Assert.DoesNotContain("로그인 후 관심 게임 추가", initialHtml);

        using (var firstFavorite = await PostFavoriteAsync(
            client,
            data.GameId,
            "Favorite",
            initialToken))
        {
            Assert.Equal(HttpStatusCode.Redirect, firstFavorite.StatusCode);
            Assert.Equal(
                $"/Games/Detail/{data.GameId}?favorite=added",
                firstFavorite.Headers.Location?.OriginalString);
        }

        using (var repeatedFavorite = await PostFavoriteAsync(
            client,
            data.GameId,
            "Favorite",
            initialToken))
        {
            Assert.Equal(HttpStatusCode.Redirect, repeatedFavorite.StatusCode);
        }

        await AssertFavoriteCountAsync(data.UserId, data.GameId, 1);

        using var favoritedDetailResponse = await client.GetAsync(
            $"/Games/Detail/{data.GameId}?favorite=added",
            TestContext.Current.CancellationToken);
        var favoritedDetailHtml = WebUtility.HtmlDecode(
            await favoritedDetailResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
        Assert.Contains("관심 게임에 추가했습니다.", favoritedDetailHtml);
        Assert.Contains(">관심 게임 제거</button>", favoritedDetailHtml);

        using var listResponse = await client.GetAsync(
            "/Account/FavoriteGames",
            TestContext.Current.CancellationToken);
        var listHtml = WebUtility.HtmlDecode(
            await listResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.Contains(data.GameTitle, listHtml);
        Assert.Contains("관심 게임 HTTP 소개", listHtml);
        Assert.Contains(data.OwnerNickname!, listHtml);
        Assert.Contains($"href=\"/Games/Detail/{data.GameId}\"", listHtml);
        Assert.Contains($"href=\"/Profiles/{data.OwnerId}\"", listHtml);

        var unfavoriteToken = PageTestSupport.ExtractAntiforgeryToken(
            favoritedDetailHtml);
        using (var firstUnfavorite = await PostFavoriteAsync(
            client,
            data.GameId,
            "Unfavorite",
            unfavoriteToken))
        {
            Assert.Equal(HttpStatusCode.Redirect, firstUnfavorite.StatusCode);
            Assert.Equal(
                $"/Games/Detail/{data.GameId}?favorite=removed",
                firstUnfavorite.Headers.Location?.OriginalString);
        }

        using (var repeatedUnfavorite = await PostFavoriteAsync(
            client,
            data.GameId,
            "Unfavorite",
            unfavoriteToken))
        {
            Assert.Equal(HttpStatusCode.Redirect, repeatedUnfavorite.StatusCode);
        }

        await AssertFavoriteCountAsync(data.UserId, data.GameId, 0);

        using var emptyListResponse = await client.GetAsync(
            "/Account/FavoriteGames",
            TestContext.Current.CancellationToken);
        var emptyListHtml = WebUtility.HtmlDecode(
            await emptyListResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
        Assert.Contains("아직 관심 게임이 없습니다.", emptyListHtml);
        Assert.DoesNotContain(data.GameTitle, emptyListHtml);
    }

    [Fact]
    public async Task Favorite_OwnGame_IsAllowed()
    {
        var data = await CreateUserAndGameAsync(userOwnsGame: true);
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, data.Email, data.Password);
        using var detailResponse = await client.GetAsync(
            $"/Games/Detail/{data.GameId}",
            TestContext.Current.CancellationToken);
        var detailHtml = await detailResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        using var response = await PostFavoriteAsync(
            client,
            data.GameId,
            "Favorite",
            PageTestSupport.ExtractAntiforgeryToken(detailHtml));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await AssertFavoriteCountAsync(data.UserId, data.GameId, 1);
    }

    [Fact]
    public async Task Favorite_MissingGame_ReturnsNotFound()
    {
        var data = await CreateUserAndGameAsync();
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, data.Email, data.Password);
        using var detailResponse = await client.GetAsync(
            $"/Games/Detail/{data.GameId}",
            TestContext.Current.CancellationToken);
        var detailHtml = await detailResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        using var response = await PostFavoriteAsync(
            client,
            long.MaxValue,
            "Favorite",
            PageTestSupport.ExtractAntiforgeryToken(detailHtml));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Favorite_WithoutAntiforgeryToken_ReturnsBadRequestAndDoesNotChangeState()
    {
        var data = await CreateUserAndGameAsync();
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, data.Email, data.Password);
        using var form = new FormUrlEncodedContent([]);

        using var response = await client.PostAsync(
            $"/Games/Detail/{data.GameId}?handler=Favorite",
            form,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertFavoriteCountAsync(data.UserId, data.GameId, 0);
    }

    private async Task<FavoritePageData> CreateUserAndGameAsync(
        bool addOwnerProfile = false,
        bool userOwnsGame = false)
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(
            _fixture);
        await using var db = _fixture.NewDbContext();
        var userId = (await TestAccountProtection.FindUserAsync(
            db,
            email,
            TestContext.Current.CancellationToken)).Id;
        var ownerId = userOwnsGame
            ? userId
            : (await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0)).Id;
        var game = await ReviewTestData.CreateGameAsync(db, ownerId);
        var gameTitle = $"Favorite HTTP {Guid.NewGuid():N}"[..24];
        game.Title = gameTitle;
        game.Description = "관심 게임 HTTP 소개";
        string? ownerNickname = null;
        if (addOwnerProfile)
        {
            ownerNickname = $"FavOwner{Guid.NewGuid():N}"[..16];
            var changedAt = DateTimeOffset.UtcNow.AddDays(-8);
            db.UserProfiles.Add(new UserProfile
            {
                UserId = ownerId,
                Nickname = ownerNickname,
                NormalizedNickname = ownerNickname.ToUpperInvariant(),
                NicknameChangedAt = changedAt,
                CreatedAt = changedAt,
                UpdatedAt = changedAt,
            });
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return new FavoritePageData(
            email,
            password,
            userId,
            ownerId,
            game.Id,
            gameTitle,
            ownerNickname);
    }

    private static async Task<HttpResponseMessage> PostFavoriteAsync(
        HttpClient client,
        long gameId,
        string handler,
        string antiforgeryToken)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiforgeryToken,
        });
        return await client.PostAsync(
            $"/Games/Detail/{gameId}?handler={handler}",
            form,
            TestContext.Current.CancellationToken);
    }

    private async Task AssertFavoriteCountAsync(
        long userId,
        long gameId,
        int expectedCount)
    {
        await using var db = _fixture.NewDbContext();
        Assert.Equal(expectedCount, await db.FavoriteGames.CountAsync(
            item => item.UserId == userId && item.GameId == gameId,
            TestContext.Current.CancellationToken));
    }

    private sealed record FavoritePageData(
        string Email,
        string Password,
        long UserId,
        long OwnerId,
        long GameId,
        string GameTitle,
        string? OwnerNickname);
}
