using System.Net;
using IndieFi.Web.Modules.Games.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class GameClassificationHttpIntegrationTests
{
    private readonly PostgreSqlFixture _fixture;

    public GameClassificationHttpIntegrationTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task OwnerFlow_CreatesDisplaysAndReplacesMultipleClassifications()
    {
        var account = await CreateAccountAsync();
        var title = $"Classification flow {Guid.NewGuid():N}";
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var ownerClient = factory.CreateClient();
        using var publicClient = factory.CreateClient();
        await PageTestSupport.LoginAsync(ownerClient, account.Email, account.Password);

        var createToken = await GetAntiforgeryTokenAsync(ownerClient, "/Games/New");
        using (var createForm = CreateGameForm(
            createToken,
            title,
            "strategy",
            ["pixel-art", "single-player"],
            ["linux", "windows"]))
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
            var game = await createdDb.Games.SingleAsync(
                item => item.Title == title,
                TestContext.Current.CancellationToken);
            gameId = game.Id;
            Assert.Equal(13, game.GenreId);
            Assert.Equal(
                [10, 28],
                await GetTagIdsAsync(createdDb, gameId));
            Assert.Equal(
                [10, 12],
                await GetPlatformIdsAsync(createdDb, gameId));
        }

        var detailPath = $"/Games/Detail/{gameId}";
        var createdDetail = await GetHtmlAsync(publicClient, detailPath);
        Assert.Contains("전략", createdDetail);
        AssertInOrder(createdDetail, "싱글플레이", "픽셀 아트");
        AssertInOrder(createdDetail, "Windows", "Linux");

        var updatedTitle = $"Updated classification {Guid.NewGuid():N}";
        var editPath = $"/Games/Edit/{gameId}";
        var editToken = await GetAntiforgeryTokenAsync(ownerClient, editPath);
        using (var editForm = CreateGameForm(
            editToken,
            updatedTitle,
            "adventure",
            ["cooperative"],
            ["web"]))
        using (var editResponse = await ownerClient.PostAsync(
            editPath,
            editForm,
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Redirect, editResponse.StatusCode);
            Assert.Equal(detailPath, editResponse.Headers.Location?.OriginalString);
        }

        await using (var updatedDb = _fixture.NewDbContext())
        {
            var game = await updatedDb.Games.SingleAsync(
                item => item.Id == gameId,
                TestContext.Current.CancellationToken);
            Assert.Equal(updatedTitle, game.Title);
            Assert.Equal(11, game.GenreId);
            Assert.Equal([14], await GetTagIdsAsync(updatedDb, gameId));
            Assert.Equal([13], await GetPlatformIdsAsync(updatedDb, gameId));
        }

        var updatedDetail = await GetHtmlAsync(publicClient, detailPath);
        Assert.Contains("어드벤처", updatedDetail);
        Assert.Contains("협동", updatedDetail);
        Assert.Contains("Web", updatedDetail);
        Assert.DoesNotContain("픽셀 아트", updatedDetail);
        Assert.DoesNotContain("Linux", updatedDetail);
    }

    [Fact]
    public async Task Post_NewGame_WithMissingRequiredClassifications_ReportsErrorsAndWritesNothing()
    {
        var account = await CreateAccountAsync();
        var title = $"Missing classifications {Guid.NewGuid():N}";
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, account.Email, account.Password);
        var token = await GetAntiforgeryTokenAsync(client, "/Games/New");

        using var form = CreateGameForm(token, title, null, [], []);
        using var response = await client.PostAsync(
            "/Games/New",
            form,
            TestContext.Current.CancellationToken);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("장르를 선택해 주세요.", html);
        Assert.Contains("지원 플랫폼을 하나 이상 선택해 주세요.", html);
        Assert.Contains("data-valmsg-for=\"GenreCode\"", html);
        Assert.Contains("data-valmsg-for=\"PlatformCodes\"", html);
        await AssertGameDoesNotExistAsync(title);
    }

    [Fact]
    public async Task Post_NewGame_WithForgedGenreAndExcessiveCollections_ReportsErrorsAndWritesNothing()
    {
        var account = await CreateAccountAsync();
        var title = $"Forged classifications {Guid.NewGuid():N}";
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, account.Email, account.Password);
        var token = await GetAntiforgeryTokenAsync(client, "/Games/New");

        using var form = CreateGameForm(
            token,
            title,
            "FORGED!",
            [
                "single-player",
                "multiplayer",
                "online-multiplayer",
                "local-multiplayer",
                "cooperative",
                "pvp",
                "story-rich",
                "exploration",
                "turn-based",
            ],
            [
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
                "FORGED!",
            ]);
        using var response = await client.PostAsync(
            "/Games/New",
            form,
            TestContext.Current.CancellationToken);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("선택할 수 없는 장르입니다.", html);
        Assert.Contains("태그는 최대 8개까지 선택할 수 있습니다.", html);
        Assert.Contains("지원 플랫폼은 최대 12개까지 선택할 수 있습니다.", html);
        await AssertGameDoesNotExistAsync(title);
    }

    [Fact]
    public async Task Post_NewGame_WithDuplicateAndForgedCollectionCodes_ReportsErrorsAndWritesNothing()
    {
        var account = await CreateAccountAsync();
        var title = $"Duplicate classifications {Guid.NewGuid():N}";
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, account.Email, account.Password);
        var token = await GetAntiforgeryTokenAsync(client, "/Games/New");

        using var form = CreateGameForm(
            token,
            title,
            "action",
            ["single-player", "single-player"],
            ["windows", "FORGED!"]);
        using var response = await client.PostAsync(
            "/Games/New",
            form,
            TestContext.Current.CancellationToken);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("같은 태그를 중복해서 선택할 수 없습니다.", html);
        Assert.Contains("선택할 수 없는 지원 플랫폼이 포함되어 있습니다.", html);
        await AssertGameDoesNotExistAsync(title);
    }

    [Fact]
    public async Task EditGame_PreservesCurrentInactiveValuesThenAllowsActiveReplacement()
    {
        var account = await CreateAccountAsync();
        long gameId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var game = await ReviewTestData.CreateGameAsync(arrangeDb, account.UserId);
            arrangeDb.GamePlatformAssignments.Add(new GamePlatformAssignment
            {
                GameId = game.Id,
                PlatformId = GameClassificationCatalog.UnspecifiedPlatformId,
            });
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            gameId = game.Id;
        }

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, account.Email, account.Password);
        var editPath = $"/Games/Edit/{gameId}";
        var editHtml = await GetHtmlAsync(client, editPath);
        Assert.Contains("미지정 (현재 비활성)", editHtml);
        Assert.Contains("value=\"unspecified\"", editHtml);

        var preserveToken = PageTestSupport.ExtractAntiforgeryToken(editHtml);
        using (var preserveForm = CreateGameForm(
            preserveToken,
            "Preserved inactive classification",
            "unspecified",
            [],
            ["unspecified"]))
        using (var preserveResponse = await client.PostAsync(
            editPath,
            preserveForm,
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Redirect, preserveResponse.StatusCode);
        }

        await using (var preservedDb = _fixture.NewDbContext())
        {
            var game = await preservedDb.Games.SingleAsync(
                item => item.Id == gameId,
                TestContext.Current.CancellationToken);
            Assert.Equal(GameClassificationCatalog.UnspecifiedGenreId, game.GenreId);
            Assert.Equal(
                [GameClassificationCatalog.UnspecifiedPlatformId],
                await GetPlatformIdsAsync(preservedDb, gameId));
        }

        var replaceToken = await GetAntiforgeryTokenAsync(client, editPath);
        using (var replaceForm = CreateGameForm(
            replaceToken,
            "Replaced active classification",
            "action",
            ["single-player"],
            ["windows"]))
        using (var replaceResponse = await client.PostAsync(
            editPath,
            replaceForm,
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Redirect, replaceResponse.StatusCode);
        }

        await using var replacedDb = _fixture.NewDbContext();
        var replacedGame = await replacedDb.Games.SingleAsync(
            item => item.Id == gameId,
            TestContext.Current.CancellationToken);
        Assert.Equal(10, replacedGame.GenreId);
        Assert.Equal([10], await GetTagIdsAsync(replacedDb, gameId));
        Assert.Equal([10], await GetPlatformIdsAsync(replacedDb, gameId));
    }

    [Fact]
    public async Task Post_EditGame_WithUnselectedInactiveCodes_PreservesAllStoredValues()
    {
        var account = await CreateAccountAsync();
        long gameId;
        string originalTitle;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var game = await ReviewTestData.CreateGameAsync(arrangeDb, account.UserId);
            game.GenreId = 10;
            arrangeDb.GameTagAssignments.Add(new GameTagAssignment
            {
                GameId = game.Id,
                TagId = 10,
            });
            arrangeDb.GamePlatformAssignments.Add(new GamePlatformAssignment
            {
                GameId = game.Id,
                PlatformId = 10,
            });
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            gameId = game.Id;
            originalTitle = game.Title;
        }

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, account.Email, account.Password);
        var editPath = $"/Games/Edit/{gameId}";
        var token = await GetAntiforgeryTokenAsync(client, editPath);

        using var form = CreateGameForm(
            token,
            "Must not be stored",
            "unspecified",
            ["pixel-art"],
            ["unspecified"]);
        using var response = await client.PostAsync(
            editPath,
            form,
            TestContext.Current.CancellationToken);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("선택할 수 없는 장르입니다.", html);
        Assert.Contains("선택할 수 없는 지원 플랫폼이 포함되어 있습니다.", html);

        await using var verificationDb = _fixture.NewDbContext();
        var storedGame = await verificationDb.Games.SingleAsync(
            item => item.Id == gameId,
            TestContext.Current.CancellationToken);
        Assert.Equal(originalTitle, storedGame.Title);
        Assert.Equal(10, storedGame.GenreId);
        Assert.Equal([10], await GetTagIdsAsync(verificationDb, gameId));
        Assert.Equal([10], await GetPlatformIdsAsync(verificationDb, gameId));
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

    private async Task AssertGameDoesNotExistAsync(string title)
    {
        await using var db = _fixture.NewDbContext();
        Assert.False(await db.Games.AnyAsync(
            game => game.Title == title,
            TestContext.Current.CancellationToken));
    }

    private static Task<List<long>> GetTagIdsAsync(
        Data.AppDbContext db,
        long gameId)
    {
        return db.GameTagAssignments
            .Where(item => item.GameId == gameId)
            .OrderBy(item => item.TagId)
            .Select(item => item.TagId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private static Task<List<long>> GetPlatformIdsAsync(
        Data.AppDbContext db,
        long gameId)
    {
        return db.GamePlatformAssignments
            .Where(item => item.GameId == gameId)
            .OrderBy(item => item.PlatformId)
            .Select(item => item.PlatformId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string path)
    {
        var html = await GetHtmlAsync(client, path);
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
        string? genreCode,
        IReadOnlyList<string> tagCodes,
        IReadOnlyList<string> platformCodes)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("Title", title),
            new("Description", string.Empty),
            new("__RequestVerificationToken", token),
        };
        if (genreCode is not null)
        {
            fields.Add(new("GenreCode", genreCode));
        }

        fields.AddRange(tagCodes.Select(code =>
            new KeyValuePair<string, string>("TagCodes", code)));
        fields.AddRange(platformCodes.Select(code =>
            new KeyValuePair<string, string>("PlatformCodes", code)));
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
}
