using System.Net;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.KeyDistributions.Domain;
using Microsoft.EntityFrameworkCore;
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

    [Fact]
    public async Task Get_DropDetailPage_ShowsEncryptedKeyOnlyToRecipient()
    {
        const string Secret = "HTTP-ENCRYPTED-GRANTED-KEY";
        var (email, password) = await PageTestSupport
            .CreateUserWithKnownPasswordAsync(_fixture);
        var dropId = await CreateEncryptedGrantAsync(email, Secret);

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var anonymousClient = factory.CreateClient();
        using var anonymousResponse = await anonymousClient.GetAsync(
            $"/KeyDrops/Detail/{dropId}",
            TestContext.Current.CancellationToken);
        var anonymousHtml = await anonymousResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        using var recipientClient = factory.CreateClient();
        await PageTestSupport.LoginAsync(recipientClient, email, password);
        using var recipientResponse = await recipientClient.GetAsync(
            $"/KeyDrops/Detail/{dropId}",
            TestContext.Current.CancellationToken);
        var recipientHtml = await recipientResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, anonymousResponse.StatusCode);
        Assert.DoesNotContain(Secret, anonymousHtml);
        Assert.Equal(HttpStatusCode.OK, recipientResponse.StatusCode);
        Assert.Contains(Secret, recipientHtml);
        Assert.True(recipientResponse.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Get_NewDropPage_LoadsOnlyItsTypeScriptModule()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        var gameId = await CreateGameOwnedByAsync(email);

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);

        using var response = await client.GetAsync(
            $"/KeyDrops/New?gameId={gameId}",
            TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("data-key-drop-form", html);
        Assert.Contains("type=\"module\"", html);
        Assert.Contains("/js/dist/key-distributions/new", html);
        Assert.DoesNotContain("/js/site.js", html);

        using var moduleResponse = await client.GetAsync(
            "/js/dist/key-distributions/new.js",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, moduleResponse.StatusCode);
    }

    // Service-level tests cannot see this: an annotation on the page can fail model
    // binding and return before the service runs, so only one message ever reaches
    // the screen. This drives the real form to prove both problems are reported.
    [Fact]
    public async Task Post_NewDrop_WithBlankKeysAndPastOpenTime_ShowsBothMessages()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        var gameId = await CreateGameOwnedByAsync(email);

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);

        using var formResponse = await client.GetAsync(
            $"/KeyDrops/New?gameId={gameId}",
            TestContext.Current.CancellationToken);
        var formHtml = await formResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        var antiforgeryToken = PageTestSupport.ExtractAntiforgeryToken(formHtml);

        using var scheduleForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["OpensAt"] = "2020-01-01T00:00",
            ["KeysText"] = string.Empty,
            ["__RequestVerificationToken"] = antiforgeryToken,
        });
        using var scheduleResponse = await client.PostAsync(
            $"/KeyDrops/New?gameId={gameId}",
            scheduleForm,
            TestContext.Current.CancellationToken);
        var scheduleHtml = WebUtility.HtmlDecode(
            await scheduleResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, scheduleResponse.StatusCode);
        Assert.Contains("키를 한 개 이상 입력해 주세요.", scheduleHtml);
        Assert.Contains("오픈 시각은 현재보다 미래여야 합니다.", scheduleHtml);
    }

    private async Task<long> CreateGameOwnedByAsync(string ownerEmail)
    {
        await using var db = _fixture.NewDbContext();
        var cancellationToken = TestContext.Current.CancellationToken;

        var owner = await TestAccountProtection.FindUserAsync(
            db,
            ownerEmail,
            cancellationToken);

        var game = new Game
        {
            OwnerId = owner.Id,
            Title = $"Test Game {Guid.NewGuid():N}"
        };

        db.Games.Add(game);
        await db.SaveChangesAsync(cancellationToken);

        return game.Id;
    }

    private async Task<long> CreateEncryptedGrantAsync(
        string recipientEmail,
        string secret)
    {
        await using var db = _fixture.NewDbContext();
        var cancellationToken = TestContext.Current.CancellationToken;
        var recipient = await TestAccountProtection.FindUserAsync(
            db,
            recipientEmail,
            cancellationToken);
        var owner = TestAccountProtection.AddUser(
            db,
            $"{Guid.NewGuid():N}@example.test");
        await db.SaveChangesAsync(cancellationToken);
        var game = new Game
        {
            OwnerId = owner.Id,
            Title = $"Test Game {Guid.NewGuid():N}",
        };
        db.Games.Add(game);
        await db.SaveChangesAsync(cancellationToken);
        var drop = new KeyDrop
        {
            GameId = game.Id,
            OwnerId = owner.Id,
            OpensAt = DateTimeOffset.UtcNow.AddHours(-1),
            TotalKeys = 1,
        };
        db.KeyDrops.Add(drop);
        await db.SaveChangesAsync(cancellationToken);

        var parsed = GameKeyPolicy.ParseBatch(secret);
        var gameKeyValue = Assert.Single(parsed.Keys);
        var protection = TestAccountProtection.CreateGameKeyProtectionService();
        var protectedKey = protection.Protect(gameKeyValue);
        var gameKey = new GameKey
        {
            DropId = drop.Id,
            Ciphertext = protectedKey.Ciphertext,
            Nonce = protectedKey.Nonce,
            AuthenticationTag = protectedKey.AuthenticationTag,
            EncryptionKeyId = protectedKey.EncryptionKeyId,
        };
        db.GameKeys.Add(gameKey);
        foreach (var fingerprint in protection.CreateWritableFingerprints(gameKeyValue))
        {
            db.GameKeyFingerprints.Add(new StoredGameKeyFingerprint
            {
                GameKey = gameKey,
                KeyId = fingerprint.KeyId,
                Value = fingerprint.Value,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        db.KeyGrants.Add(new KeyGrant
        {
            DropId = drop.Id,
            UserId = recipient.Id,
            KeyId = gameKey.Id,
        });
        await db.SaveChangesAsync(cancellationToken);
        return drop.Id;
    }

    private async Task LoginAsync(HttpClient client)
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        await PageTestSupport.LoginAsync(client, email, password);
    }

    private async Task<(long DropId, string GameTitle)> CreateOpenDropAsync(int keyCount)
    {
        await using var db = _fixture.NewDbContext();
        var cancellationToken = TestContext.Current.CancellationToken;

        var owner = TestAccountProtection.AddUser(
            db,
            $"{Guid.NewGuid():N}@example.test");
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
            db.GameKeys.Add(new GameKey
            {
                DropId = drop.Id,
                KeyValue = $"TEST-{Guid.NewGuid():N}"
            });
        }

        await db.SaveChangesAsync(cancellationToken);

        return (drop.Id, game.Title);
    }
}
