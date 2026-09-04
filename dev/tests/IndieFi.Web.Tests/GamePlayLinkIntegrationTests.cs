using System.Net;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class GamePlayLinkIntegrationTests
{
    private readonly PostgreSqlFixture _fixture;

    public GamePlayLinkIntegrationTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task OwnerFlow_CreatesPublishesThenEditsReordersAndRemovesLinks()
    {
        var account = await CreateAccountAsync();
        var title = $"Integration game {Guid.NewGuid():N}";
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var ownerClient = factory.CreateClient();
        using var publicClient = factory.CreateClient();
        await PageTestSupport.LoginAsync(ownerClient, account.Email, account.Password);

        var createToken = await GetAntiforgeryTokenAsync(ownerClient, "/Games/New");
        using (var createForm = CreateGameForm(
            createToken,
            title,
            [
                new LinkField("Alpha portal", "DeveloperWebsite", "Web", "https://example.com/alpha"),
                new LinkField("Beta download", "ItchIo", "Windows", "https://example.com/beta"),
                new LinkField("Gamma store", "Steam", "", "https://example.com/gamma"),
            ]))
        using (var createResponse = await ownerClient.PostAsync(
            "/Games/New",
            createForm,
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Redirect, createResponse.StatusCode);
        }

        long gameId;
        await using (var createdDb = _fixture.NewDbContext())
        {
            gameId = await createdDb.Games
                .Where(game => game.Title == title)
                .Select(game => game.Id)
                .SingleAsync(TestContext.Current.CancellationToken);
        }

        var detailPath = $"/Games/Detail/{gameId}";
        var createdDetail = await GetHtmlAsync(publicClient, detailPath);
        AssertInOrder(createdDetail, "Alpha portal", "Beta download", "Gamma store");

        var editPath = $"/Games/Edit/{gameId}";
        var editToken = await GetAntiforgeryTokenAsync(ownerClient, editPath);
        using (var editForm = CreateGameForm(
            editToken,
            title,
            [
                new LinkField("Gamma updated", "Steam", "Multiple", "https://example.com/gamma-new"),
                new LinkField("Alpha portal", "DeveloperWebsite", "Web", "https://example.com/alpha"),
            ]))
        using (var editResponse = await ownerClient.PostAsync(
            editPath,
            editForm,
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Redirect, editResponse.StatusCode);
        }

        await using (var verificationDb = _fixture.NewDbContext())
        {
            var links = await verificationDb.GamePlayLinks
                .Where(link => link.GameId == gameId)
                .OrderBy(link => link.SortOrder)
                .ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(["Gamma updated", "Alpha portal"], links.Select(link => link.DisplayName));
            Assert.Equal([0, 1], links.Select(link => link.SortOrder));
            Assert.DoesNotContain(links, link => link.DisplayName == "Beta download");
        }

        var updatedDetail = await GetHtmlAsync(publicClient, detailPath);
        AssertInOrder(updatedDetail, "Gamma updated", "Alpha portal");
        Assert.DoesNotContain("Beta download", updatedDetail);
        Assert.Contains("https://example.com/gamma-new", updatedDetail);
    }

    [Fact]
    public async Task Post_NewGame_WithTooManyAndUnsafeLinks_ReportsPoliciesAndWritesNothing()
    {
        var account = await CreateAccountAsync();
        var title = $"Rejected links {Guid.NewGuid():N}";
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, account.Email, account.Password);
        var token = await GetAntiforgeryTokenAsync(client, "/Games/New");

        using var form = CreateGameForm(
            token,
            title,
            [
                new LinkField("Public one", "Other", "", "https://example.com/one"),
                new LinkField("Public two", "Other", "", "https://example.com/two"),
                new LinkField("Script", "Other", "", "javascript:alert(1)"),
                new LinkField("Local", "Other", "", "https://localhost/play"),
                new LinkField("Credentials", "Other", "", "https://user:secret@example.com/play"),
                new LinkField("Public six", "Other", "", "https://example.com/six"),
            ]);
        using var response = await client.PostAsync(
            "/Games/New",
            form,
            TestContext.Current.CancellationToken);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("외부 플레이 링크는 최대 5개까지 등록할 수 있습니다.", html);
        Assert.Contains("외부 플레이 주소는 HTTP 또는 HTTPS 주소여야 합니다.", html);
        Assert.Contains("공개 인터넷 호스트의 외부 플레이 주소를 입력해 주세요.", html);
        Assert.Contains("사용자 정보가 포함된 외부 플레이 주소는 사용할 수 없습니다.", html);
        Assert.Contains("data-valmsg-for=\"Links[2].Url\"", html);
        Assert.Contains("data-valmsg-for=\"Links[3].Url\"", html);
        Assert.Contains("data-valmsg-for=\"Links[4].Url\"", html);

        await using var verificationDb = _fixture.NewDbContext();
        Assert.False(await verificationDb.Games.AnyAsync(
            game => game.Title == title,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Post_EditGame_AsAnotherUser_ReturnsNotFoundAndPreservesLinks()
    {
        var owner = await CreateAccountAsync();
        var otherUser = await CreateAccountAsync();
        var game = await CreateGameWithLinkAsync(owner.UserId, "Owner link");
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, otherUser.Email, otherUser.Password);
        var token = await GetAntiforgeryTokenAsync(client, "/Games/New");

        using var form = CreateGameForm(
            token,
            "Attacker title",
            [new LinkField("Attacker link", "Other", "", "https://example.com/attacker")]);
        using var response = await client.PostAsync(
            $"/Games/Edit/{game.GameId}",
            form,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await using var verificationDb = _fixture.NewDbContext();
        var verifiedGame = await verificationDb.Games.SingleAsync(
            item => item.Id == game.GameId,
            TestContext.Current.CancellationToken);
        var verifiedLink = await verificationDb.GamePlayLinks.SingleAsync(
            link => link.GameId == game.GameId,
            TestContext.Current.CancellationToken);
        Assert.Equal(game.Title, verifiedGame.Title);
        Assert.Equal(1, verifiedGame.GenreId);
        Assert.Equal("Owner link", verifiedLink.DisplayName);
        Assert.Empty(await verificationDb.GameTagAssignments
            .Where(item => item.GameId == game.GameId)
            .ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await verificationDb.GamePlatformAssignments
            .Where(item => item.GameId == game.GameId)
            .ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SuspendedExistingSession_CannotEditGameLinks()
    {
        var owner = await CreateAccountAsync();
        var game = await CreateGameWithLinkAsync(owner.UserId, "Original link");
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, owner.Email, owner.Password);
        var editPath = $"/Games/Edit/{game.GameId}";
        var token = await GetAntiforgeryTokenAsync(client, editPath);

        await using (var suspensionDb = _fixture.NewDbContext())
        {
            var user = await suspensionDb.Users.SingleAsync(
                item => item.Id == owner.UserId,
                TestContext.Current.CancellationToken);
            user.AccountStatus = AccountStatus.IndefinitelySuspended;
            user.SuspendedUntil = null;
            await suspensionDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var form = CreateGameForm(
            token,
            "Blocked title",
            [new LinkField("Blocked link", "Other", "", "https://example.com/blocked")]);
        using var response = await client.PostAsync(
            editPath,
            form,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Login", response.Headers.Location?.AbsolutePath);

        await using var verificationDb = _fixture.NewDbContext();
        var verifiedGame = await verificationDb.Games.SingleAsync(
            item => item.Id == game.GameId,
            TestContext.Current.CancellationToken);
        var verifiedLink = await verificationDb.GamePlayLinks.SingleAsync(
            link => link.GameId == game.GameId,
            TestContext.Current.CancellationToken);
        Assert.Equal(game.Title, verifiedGame.Title);
        Assert.Equal("Original link", verifiedLink.DisplayName);
    }

    private async Task<TestAccount> CreateAccountAsync()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var userId = (await TestAccountProtection.FindUserAsync(
            db,
            email,
            TestContext.Current.CancellationToken)).Id;
        return new TestAccount(userId, email, password);
    }

    private async Task<TestGame> CreateGameWithLinkAsync(long ownerId, string displayName)
    {
        await using var db = _fixture.NewDbContext();
        var game = await ReviewTestData.CreateGameAsync(db, ownerId);
        db.GamePlayLinks.Add(new GamePlayLink
        {
            GameId = game.Id,
            DisplayName = displayName,
            Provider = GamePlayLinkProvider.Other,
            Url = "https://example.com/original",
            SortOrder = 0,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return new TestGame(game.Id, game.Title);
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return PageTestSupport.ExtractAntiforgeryToken(html);
    }

    private static async Task<string> GetHtmlAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return html;
    }

    private static FormUrlEncodedContent CreateGameForm(
        string token,
        string title,
        IReadOnlyList<LinkField> links)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("Title", title),
            new("Description", string.Empty),
            new("GenreCode", "action"),
            new("PlatformCodes", "windows"),
            new("__RequestVerificationToken", token),
        };

        for (var index = 0; index < links.Count; index++)
        {
            var link = links[index];
            fields.Add(new($"Links[{index}].DisplayName", link.DisplayName));
            fields.Add(new($"Links[{index}].Provider", link.Provider));
            fields.Add(new($"Links[{index}].TargetPlatform", link.TargetPlatform));
            fields.Add(new($"Links[{index}].Url", link.Url));
        }

        return new FormUrlEncodedContent(fields);
    }

    private static void AssertInOrder(string source, params string[] values)
    {
        var previousPosition = -1;
        foreach (var value in values)
        {
            var position = source.IndexOf(value, StringComparison.Ordinal);
            Assert.True(position > previousPosition, $"Expected '{value}' after the previous value.");
            previousPosition = position;
        }
    }

    private sealed record TestAccount(long UserId, string Email, string Password);

    private sealed record TestGame(long GameId, string Title);

    private sealed record LinkField(
        string DisplayName,
        string Provider,
        string TargetPlatform,
        string Url);
}
