using System.Net;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Notifications.Application;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class KeyDropSubscriptionServiceTests
{
    private readonly PostgreSqlFixture _fixture;

    public KeyDropSubscriptionServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SubscribeAndUnsubscribe_RepeatedRequestsAreIdempotent()
    {
        var (userId, dropId) = await CreateScheduledDropAsync();
        await using var db = _fixture.NewDbContext();
        var service = new KeyDropSubscriptionService(db);

        var firstSubscribe = await service.SubscribeAsync(
            userId,
            dropId,
            TestContext.Current.CancellationToken);
        var secondSubscribe = await service.SubscribeAsync(
            userId,
            dropId,
            TestContext.Current.CancellationToken);
        var firstUnsubscribe = await service.UnsubscribeAsync(
            userId,
            dropId,
            TestContext.Current.CancellationToken);
        var secondUnsubscribe = await service.UnsubscribeAsync(
            userId,
            dropId,
            TestContext.Current.CancellationToken);

        Assert.Equal(KeyDropSubscriptionOutcome.Subscribed, firstSubscribe.Outcome);
        Assert.True(firstSubscribe.Changed);
        Assert.Equal(KeyDropSubscriptionOutcome.Subscribed, secondSubscribe.Outcome);
        Assert.False(secondSubscribe.Changed);
        Assert.Equal(KeyDropSubscriptionOutcome.Unsubscribed, firstUnsubscribe.Outcome);
        Assert.True(firstUnsubscribe.Changed);
        Assert.Equal(KeyDropSubscriptionOutcome.Unsubscribed, secondUnsubscribe.Outcome);
        Assert.False(secondUnsubscribe.Changed);
        Assert.False(await service.IsSubscribedAsync(
            userId,
            dropId,
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Subscribe_OpenedOrClosedDrop_IsRejected(bool closeEarly)
    {
        var (userId, dropId) = await CreateScheduledDropAsync();
        await using (var setupDb = _fixture.NewDbContext())
        {
            var drop = await setupDb.KeyDrops.SingleAsync(
                item => item.Id == dropId,
                TestContext.Current.CancellationToken);
            if (closeEarly)
            {
                drop.ClosedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                drop.OpensAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            }

            await setupDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var db = _fixture.NewDbContext();
        var result = await new KeyDropSubscriptionService(db).SubscribeAsync(
            userId,
            dropId,
            TestContext.Current.CancellationToken);

        Assert.Equal(KeyDropSubscriptionOutcome.NotScheduled, result.Outcome);
        Assert.False(await db.KeyDropSubscriptions.AnyAsync(
            item => item.UserId == userId && item.DropId == dropId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Subscribe_SuspendedAccount_IsRejected()
    {
        var (userId, dropId) = await CreateScheduledDropAsync();
        await using (var setupDb = _fixture.NewDbContext())
        {
            var transitions = new AccountStatusTransitionService(
                setupDb,
                TimeProvider.System);
            Assert.Equal(
                AccountStatusTransitionOutcome.Changed,
                (await transitions.SuspendIndefinitelyAsync(
                    userId,
                    TestContext.Current.CancellationToken)).Outcome);
        }

        await using var db = _fixture.NewDbContext();
        var result = await new KeyDropSubscriptionService(db).SubscribeAsync(
            userId,
            dropId,
            TestContext.Current.CancellationToken);

        Assert.Equal(KeyDropSubscriptionOutcome.AccountUnavailable, result.Outcome);
        Assert.False(await db.KeyDropSubscriptions.AnyAsync(
            item => item.UserId == userId && item.DropId == dropId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DetailPage_SubscribeAndUnsubscribe_UsesCsrfAndShowsGuaranteeNotice()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(
            _fixture);
        await using var lookupDb = _fixture.NewDbContext();
        var userId = (await TestAccountProtection.FindUserAsync(
            lookupDb,
            email,
            TestContext.Current.CancellationToken)).Id;
        var (_, dropId) = await CreateScheduledDropAsync(userId);
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);

        using var page = await client.GetAsync(
            $"/KeyDrops/Detail/{dropId}",
            TestContext.Current.CancellationToken);
        var html = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));
        Assert.Contains("오픈 알림 신청", html);
        Assert.Contains("게임 키 수령 기회를 보장하지 않습니다.", html);
        var token = PageTestSupport.ExtractAntiforgeryToken(html);

        using var subscribe = await client.PostAsync(
            $"/KeyDrops/Detail/{dropId}?handler=SubscribeNotification",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
            }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, subscribe.StatusCode);
        Assert.Contains("notification=subscribed", subscribe.Headers.Location?.OriginalString);

        using var subscribedPage = await client.GetAsync(
            subscribe.Headers.Location,
            TestContext.Current.CancellationToken);
        var subscribedHtml = WebUtility.HtmlDecode(
            await subscribedPage.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
        Assert.Contains("배포 오픈 알림을 신청했습니다.", subscribedHtml);
        Assert.Contains("오픈 알림 신청 취소", subscribedHtml);
        Assert.True(await IsSubscribedAsync(userId, dropId));

        using var unsubscribe = await client.PostAsync(
            $"/KeyDrops/Detail/{dropId}?handler=UnsubscribeNotification",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] =
                    PageTestSupport.ExtractAntiforgeryToken(subscribedHtml),
            }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, unsubscribe.StatusCode);
        Assert.False(await IsSubscribedAsync(userId, dropId));
    }

    private async Task<(long UserId, long DropId)> CreateScheduledDropAsync(
        long? existingUserId = null)
    {
        await using var db = _fixture.NewDbContext();
        var userId = existingUserId
            ?? (await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0)).Id;
        var owner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var game = await ReviewTestData.CreateGameAsync(db, owner.Id);
        var drop = new KeyDrop
        {
            GameId = game.Id,
            OwnerId = owner.Id,
            OpensAt = DateTimeOffset.UtcNow.AddHours(1),
            TotalKeys = 1,
        };
        db.KeyDrops.Add(drop);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        TestAccountProtection.AddGameKey(
            db,
            drop,
            $"SUBSCRIPTION-{Guid.NewGuid():N}");
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (userId, drop.Id);
    }

    private async Task<bool> IsSubscribedAsync(long userId, long dropId)
    {
        await using var db = _fixture.NewDbContext();
        return await db.KeyDropSubscriptions.AnyAsync(
            item => item.UserId == userId && item.DropId == dropId,
            TestContext.Current.CancellationToken);
    }
}
