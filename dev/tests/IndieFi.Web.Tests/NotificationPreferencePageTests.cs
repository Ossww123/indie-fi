using System.Net;
using IndieFi.Web.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class NotificationPreferencePageTests
{
    private readonly PostgreSqlFixture _fixture;

    public NotificationPreferencePageTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Get_Anonymous_RedirectsToLogin()
    {
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/Account/Notifications",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(
            "/Login?ReturnUrl=%2FAccount%2FNotifications",
            response.Headers.Location?.PathAndQuery);
    }

    [Fact]
    public async Task Get_Authenticated_ShowsAllDefaultsAndLockedMandatoryChannels()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(
            _fixture);
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);

        using var response = await client.GetAsync(
            "/Account/Notifications",
            TestContext.Current.CancellationToken);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("리뷰 작업 배정", html);
        Assert.Contains("팔로우한 사용자의 새 공개 게임", html);
        Assert.Contains("계정 정지 및 해제", html);
        Assert.Contains("해제할 수 없는 계정 알림입니다.", html);
        Assert.Contains("Preferences[8].WebEnabled", html);
        Assert.Contains("disabled=\"disabled\"", html);
    }

    [Fact]
    public async Task Post_CompleteSelection_PersistsAndForcesMandatoryChannels()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(
            _fixture);
        await using var lookupDb = _fixture.NewDbContext();
        var userId = (await TestAccountProtection.FindUserAsync(
            lookupDb,
            email,
            TestContext.Current.CancellationToken)).Id;
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);
        using var page = await client.GetAsync(
            "/Account/Notifications",
            TestContext.Current.CancellationToken);
        var html = await page.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        var form = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] =
                PageTestSupport.ExtractAntiforgeryToken(html),
        };
        var kinds = Enum.GetValues<NotificationKind>();
        for (var index = 0; index < kinds.Length; index++)
        {
            form[$"Preferences[{index}].Kind"] = kinds[index].ToString();
            form[$"Preferences[{index}].WebEnabled"] = "false";
            form[$"Preferences[{index}].EmailEnabled"] =
                kinds[index] == NotificationKind.ReviewExpired ? "true" : "false";
        }

        using var response = await client.PostAsync(
            "/Account/Notifications",
            new FormUrlEncodedContent(form),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Account/Notifications", response.Headers.Location?.OriginalString);
        await using var verificationDb = _fixture.NewDbContext();
        var stored = await verificationDb.NotificationPreferences
            .Where(item => item.UserId == userId)
            .OrderBy(item => item.Kind)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(kinds.Length, stored.Count);
        Assert.True(stored.Single(
            item => item.Kind == NotificationKind.ReviewExpired).EmailEnabled);
        var mandatory = stored.Single(
            item => item.Kind == NotificationKind.AccountStatusChanged);
        Assert.True(mandatory.WebEnabled);
        Assert.True(mandatory.EmailEnabled);
    }

    [Fact]
    public async Task Post_WithoutCsrf_ReturnsBadRequestAndStoresNothing()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(
            _fixture);
        await using var lookupDb = _fixture.NewDbContext();
        var userId = (await TestAccountProtection.FindUserAsync(
            lookupDb,
            email,
            TestContext.Current.CancellationToken)).Id;
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);

        using var response = await client.PostAsync(
            "/Account/Notifications",
            new FormUrlEncodedContent([]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var verificationDb = _fixture.NewDbContext();
        Assert.False(await verificationDb.NotificationPreferences.AnyAsync(
            item => item.UserId == userId,
            TestContext.Current.CancellationToken));
    }
}
