using System.Net;
using IndieFi.Web.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class NotificationInboxPageTests
{
    private readonly PostgreSqlFixture _fixture;

    public NotificationInboxPageTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Get_Anonymous_RedirectsToLogin()
    {
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/Notifications",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(
            "/Login?ReturnUrl=%2FNotifications",
            response.Headers.Location?.PathAndQuery);
    }

    [Fact]
    public async Task Get_Authenticated_ShowsOnlyOwnedNotificationAndNavigationBadge()
    {
        var user = await CreateUserAsync();
        var other = await CreateUserAsync();
        var title = $"Notification HTTP {Guid.NewGuid():N}";
        await AddNotificationAsync(user.UserId, title, "<script>alert('x')</script>");
        await AddNotificationAsync(other.UserId, "Other notification", "private");
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, user.Email, user.Password);

        using var response = await client.GetAsync(
            "/Notifications",
            TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(title, WebUtility.HtmlDecode(html));
        Assert.DoesNotContain("Other notification", html);
        Assert.DoesNotContain("<script>alert", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("href=\"/Reviews\"", html);
        Assert.Contains("data-notification-badge", html);
        Assert.Contains("data-notification-live-state", html);
        Assert.Contains("최신 알림 확인 중", WebUtility.HtmlDecode(html));
        Assert.Contains("ivory-inbox-item-unread", html);
        Assert.Contains("읽지 않은 알림 1개", WebUtility.HtmlDecode(html));
    }

    [Fact]
    public async Task Post_SetReadAndReadAll_UsesCsrfAndChangesOwnedState()
    {
        var user = await CreateUserAsync();
        var firstId = await AddNotificationAsync(user.UserId, "First", "body");
        var secondId = await AddNotificationAsync(user.UserId, "Second", "body");
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, user.Email, user.Password);
        using var pageResponse = await client.GetAsync(
            "/Notifications",
            TestContext.Current.CancellationToken);
        var html = await pageResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        var token = PageTestSupport.ExtractAntiforgeryToken(html);

        using (var readResponse = await PostAsync(
            client,
            "SetRead",
            token,
            new()
            {
                ["id"] = firstId.ToString(),
                ["isRead"] = "true",
            }))
        {
            Assert.Equal(HttpStatusCode.Redirect, readResponse.StatusCode);
            Assert.Equal(
                "/Notifications?status=read",
                readResponse.Headers.Location?.OriginalString);
        }

        using (var allResponse = await PostAsync(
            client,
            "ReadAll",
            token,
            []))
        {
            Assert.Equal(HttpStatusCode.Redirect, allResponse.StatusCode);
        }

        await using var verificationDb = _fixture.NewDbContext();
        Assert.Equal(2, await verificationDb.Notifications.CountAsync(
            item => (item.Id == firstId || item.Id == secondId)
                && item.ReadAt != null,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Post_WithoutCsrf_ReturnsBadRequestAndDoesNotChangeState()
    {
        var user = await CreateUserAsync();
        var notificationId = await AddNotificationAsync(user.UserId, "CSRF", "body");
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, user.Email, user.Password);

        using var response = await client.PostAsync(
            "/Notifications?handler=SetRead",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["id"] = notificationId.ToString(),
                ["isRead"] = "true",
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var verificationDb = _fixture.NewDbContext();
        Assert.Null(await verificationDb.Notifications
            .Where(item => item.Id == notificationId)
            .Select(item => item.ReadAt)
            .SingleAsync(TestContext.Current.CancellationToken));
    }

    private async Task<(string Email, string Password, long UserId)> CreateUserAsync()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(
            _fixture);
        await using var db = _fixture.NewDbContext();
        var userId = (await TestAccountProtection.FindUserAsync(
            db,
            email,
            TestContext.Current.CancellationToken)).Id;
        return (email, password, userId);
    }

    private async Task<long> AddNotificationAsync(
        long userId,
        string title,
        string body)
    {
        var now = DateTimeOffset.UtcNow;
        var notification = new Notification
        {
            UserId = userId,
            EventId = Guid.NewGuid(),
            Kind = NotificationKind.ReviewAssigned,
            SourceType = "review_assignment",
            SourceId = Random.Shared.NextInt64(1, long.MaxValue),
            Title = title,
            Body = body,
            DestinationPath = "/Reviews",
            IsWebVisible = true,
            OccurredAt = now,
            CreatedAt = now,
            ExpiresAt = now.AddDays(90),
        };
        await using var db = _fixture.NewDbContext();
        db.Notifications.Add(notification);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return notification.Id;
    }

    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string handler,
        string token,
        Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = token;
        return client.PostAsync(
            $"/Notifications?handler={handler}",
            new FormUrlEncodedContent(fields),
            TestContext.Current.CancellationToken);
    }
}
