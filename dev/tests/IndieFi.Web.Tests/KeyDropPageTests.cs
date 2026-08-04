using System.Net;
using IndieFi.Web.Entities;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class KeyDropPageTests
{
    private readonly PostgreSqlFixture _fixture;

    public KeyDropPageTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    // This first page test proves that the HTTP foundation crosses routing, Razor Pages, and the real database.
    [Fact]
    public async Task Get_DropDetailPage_ShowsGameTitleAndRemainingKeys()
    {
        var (dropId, gameTitle) = await CreateOpenDropAsync(3);

        // One request needs no shared host lifetime, so this test owns and disposes its factory.
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/KeyDrops/Detail/{dropId}",
            TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(gameTitle, html);
        Assert.Contains("총 3개 중 3개 남음", html);
    }

    // The claim notice survives the redirect because the URL, rather than a cookie, carries its code.
    [Fact]
    public async Task Post_Claim_WhenSoldOut_ShowsMessageAfterRedirect()
    {
        var (dropId, _) = await CreateOpenDropAsync(0);

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await LoginAsync(client);

        using var detailResponse = await client.GetAsync(
            $"/KeyDrops/Detail/{dropId}",
            TestContext.Current.CancellationToken);
        var detailHtml = await detailResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        var antiforgeryToken = PageTestSupport.ExtractAntiforgeryToken(detailHtml);

        using var claimForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiforgeryToken,
        });
        using var claimResponse = await client.PostAsync(
            $"/KeyDrops/Detail/{dropId}?handler=Claim",
            claimForm,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, claimResponse.StatusCode);
        var location = claimResponse.Headers.Location;
        Assert.NotNull(location);
        Assert.Contains("claim=soldout", location.OriginalString);

        using var redirectedResponse = await client.GetAsync(
            location,
            TestContext.Current.CancellationToken);
        var redirectedHtml = await redirectedResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, redirectedResponse.StatusCode);

        // Razor writes non-Latin text from an expression as numeric character references,
        // so the raw response spells this message as "&#xD0A4;&#xAC00;...". Decoding compares
        // what the reader sees instead of how it happens to be escaped.
        Assert.Contains("키가 모두 소진되었습니다.", WebUtility.HtmlDecode(redirectedHtml));
    }

    // Anyone can edit the query string, so a code we do not recognise must produce no
    // notice at all. Echoing it back would put attacker-chosen text on our own page.
    [Fact]
    public async Task Get_DropDetailPage_WithUnknownClaimCode_ShowsNoNotice()
    {
        var (dropId, _) = await CreateOpenDropAsync(3);

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();

        const string injected = "totally-made-up-code";
        using var response = await client.GetAsync(
            $"/KeyDrops/Detail/{dropId}?claim={injected}",
            TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(injected, html);
        Assert.DoesNotContain("alert-warning", html);
    }

    private async Task LoginAsync(HttpClient client)
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);

        using var loginPageResponse = await client.GetAsync(
            "/Login",
            TestContext.Current.CancellationToken);
        var loginHtml = await loginPageResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        var antiforgeryToken = PageTestSupport.ExtractAntiforgeryToken(loginHtml);

        using var loginForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["__RequestVerificationToken"] = antiforgeryToken,
        });
        using var loginResponse = await client.PostAsync(
            "/Login",
            loginForm,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
    }

    private async Task<(long DropId, string GameTitle)> CreateOpenDropAsync(int keyCount)
    {
        await using var db = _fixture.NewDbContext();
        var cancellationToken = TestContext.Current.CancellationToken;

        var owner = new User
        {
            Email = $"{Guid.NewGuid():N}@example.test",
            PasswordHash = "test-password-hash"
        };

        db.Users.Add(owner);
        await db.SaveChangesAsync(cancellationToken);

        var game = new Game
        {
            OwnerId = owner.Id,
            Title = $"Test Game {Guid.NewGuid():N}"
        };

        db.Games.Add(game);
        await db.SaveChangesAsync(cancellationToken);

        var drop = new KeyDrop
        {
            GameId = game.Id,
            OwnerId = owner.Id,
            OpensAt = DateTimeOffset.UtcNow.AddHours(-1),
            TotalKeys = keyCount
        };

        db.KeyDrops.Add(drop);
        await db.SaveChangesAsync(cancellationToken);

        for (var index = 0; index < keyCount; index++)
        {
            db.SteamKeys.Add(new SteamKey
            {
                DropId = drop.Id,
                KeyValue = $"TEST-{Guid.NewGuid():N}"
            });
        }

        await db.SaveChangesAsync(cancellationToken);

        return (drop.Id, game.Title);
    }
}
