using System.Net;
using System.Text.Json;
using IndieFi.Web.Modules.Notifications.Domain;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class NotificationSnapshotEndpointTests
{
    private readonly PostgreSqlFixture _fixture;

    public NotificationSnapshotEndpointTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Snapshot_Anonymous_RedirectsWithoutReturningData()
    {
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/Notifications?handler=Snapshot",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Login", response.Headers.Location?.AbsolutePath);
        Assert.StartsWith("?ReturnUrl=", response.Headers.Location?.Query);
    }

    [Fact]
    public async Task Snapshot_Authenticated_ReturnsOwnedAuthorityWithoutContent()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(
            _fixture);
        await using var lookupDb = _fixture.NewDbContext();
        var userId = (await TestAccountProtection.FindUserAsync(
            lookupDb,
            email,
            TestContext.Current.CancellationToken)).Id;
        var secretTitle = $"Secret title {Guid.NewGuid():N}";
        var latestId = await AddNotificationAsync(userId, secretTitle);
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);

        using var response = await client.GetAsync(
            "/Notifications?handler=Snapshot",
            TestContext.Current.CancellationToken);
        var json = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(1, document.RootElement.GetProperty("unreadCount").GetInt32());
        Assert.Equal(
            latestId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            document.RootElement.GetProperty("latestNotificationId").GetString());
        Assert.DoesNotContain(secretTitle, json);
        Assert.False(document.RootElement.TryGetProperty("userId", out _));
    }

    [Fact]
    public async Task Layout_LoadsRealtimeClientOnlyForAuthenticatedUser()
    {
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        using var anonymousResponse = await client.GetAsync(
            "/",
            TestContext.Current.CancellationToken);
        var anonymousHtml = await anonymousResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain("notifications/realtime.js", anonymousHtml);

        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(
            _fixture);
        await PageTestSupport.LoginAsync(client, email, password);
        using var authenticatedResponse = await client.GetAsync(
            "/",
            TestContext.Current.CancellationToken);
        var authenticatedHtml = await authenticatedResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains("notifications/realtime.js", authenticatedHtml);
    }

    private async Task<long> AddNotificationAsync(long userId, string title)
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
            Body = "Secret body",
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
}
