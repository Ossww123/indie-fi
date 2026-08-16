using IndieFi.Web.Data;
using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class NotificationInboxServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 16, 5, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlFixture _fixture;

    public NotificationInboxServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Get_ReturnsOnlyOwnedVisibleUnexpiredItemsInStableOrder()
    {
        var (userId, otherUserId) = await CreateUsersAsync();
        await using (var setupDb = _fixture.NewDbContext())
        {
            setupDb.Notifications.AddRange(
                Notification(userId, 1, Now.AddMinutes(-3), visible: true),
                Notification(userId, 2, Now.AddMinutes(-1), visible: true),
                Notification(userId, 3, Now.AddMinutes(-2), visible: false),
                Notification(userId, 4, Now.AddDays(-91), visible: true),
                Notification(otherUserId, 5, Now, visible: true));
            await setupDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var db = _fixture.NewDbContext();
        var snapshot = await Service(db).GetAsync(
            userId,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            ["Notification 2", "Notification 1"],
            snapshot.Items.Select(item => item.Title));
        Assert.Equal(2, snapshot.UnreadCount);
        Assert.Null(snapshot.NextCursor);
    }

    [Fact]
    public async Task Get_WithCursor_UsesCreatedAtAndIdKeyset()
    {
        var (userId, _) = await CreateUsersAsync();
        await using (var setupDb = _fixture.NewDbContext())
        {
            for (var index = 1; index <= 3; index++)
            {
                setupDb.Notifications.Add(
                    Notification(userId, index, Now, visible: true));
            }

            await setupDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var firstDb = _fixture.NewDbContext();
        var first = await Service(firstDb).GetAsync(
            userId,
            pageSize: 2,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(first.NextCursor);
        Assert.Equal(2, first.Items.Count);

        await using var secondDb = _fixture.NewDbContext();
        var second = await Service(secondDb).GetAsync(
            userId,
            pageSize: 2,
            cursor: first.NextCursor,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(second.Items);
        Assert.Empty(first.Items.Select(item => item.Id).Intersect(
            second.Items.Select(item => item.Id)));
    }

    [Fact]
    public async Task SetReadState_ChangesOnlyOwnedVisibleNotification()
    {
        var (userId, otherUserId) = await CreateUsersAsync();
        var notification = Notification(userId, 11, Now, visible: true);
        await using (var setupDb = _fixture.NewDbContext())
        {
            setupDb.Notifications.Add(notification);
            await setupDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var wrongOwnerDb = _fixture.NewDbContext())
        {
            Assert.Equal(
                NotificationReadStateOutcome.NotFound,
                await Service(wrongOwnerDb).SetReadStateAsync(
                    otherUserId,
                    notification.Id,
                    isRead: true,
                    TestContext.Current.CancellationToken));
        }

        await using (var readDb = _fixture.NewDbContext())
        {
            Assert.Equal(
                NotificationReadStateOutcome.Changed,
                await Service(readDb).SetReadStateAsync(
                    userId,
                    notification.Id,
                    isRead: true,
                    TestContext.Current.CancellationToken));
            Assert.Equal(
                NotificationReadStateOutcome.Unchanged,
                await Service(readDb).SetReadStateAsync(
                    userId,
                    notification.Id,
                    isRead: true,
                    TestContext.Current.CancellationToken));
            Assert.Equal(
                NotificationReadStateOutcome.Changed,
                await Service(readDb).SetReadStateAsync(
                    userId,
                    notification.Id,
                    isRead: false,
                    TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task MarkAllRead_UpdatesUnreadAndLeavesAlreadyReadTimestamp()
    {
        var (userId, _) = await CreateUsersAsync();
        var alreadyRead = Notification(userId, 21, Now.AddMinutes(-2), visible: true);
        alreadyRead.ReadAt = Now.AddMinutes(-1);
        await using (var setupDb = _fixture.NewDbContext())
        {
            setupDb.Notifications.AddRange(
                alreadyRead,
                Notification(userId, 22, Now, visible: true));
            await setupDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var db = _fixture.NewDbContext())
        {
            Assert.Equal(1, await Service(db).MarkAllReadAsync(
                userId,
                TestContext.Current.CancellationToken));
        }

        await using var verificationDb = _fixture.NewDbContext();
        Assert.Equal(0, await Service(verificationDb).GetUnreadCountAsync(
            userId,
            TestContext.Current.CancellationToken));
        Assert.Equal(
            Now.AddMinutes(-1),
            await verificationDb.Notifications
                .Where(item => item.Id == alreadyRead.Id)
                .Select(item => item.ReadAt)
                .SingleAsync(TestContext.Current.CancellationToken));
    }

    private NotificationInboxService Service(AppDbContext db) =>
        new(db, new FixedTimeProvider(Now));

    private async Task<(long UserId, long OtherUserId)> CreateUsersAsync()
    {
        await using var db = _fixture.NewDbContext();
        var user = await ReviewTestData.CreateUserWithCreditAsync(db, 0);
        var other = await ReviewTestData.CreateUserWithCreditAsync(db, 0);
        return (user.Id, other.Id);
    }

    private static Notification Notification(
        long userId,
        long sourceId,
        DateTimeOffset createdAt,
        bool visible) => new()
    {
        UserId = userId,
        EventId = Guid.NewGuid(),
        Kind = NotificationKind.ReviewAssigned,
        SourceType = "review_assignment",
        SourceId = sourceId,
        Title = $"Notification {sourceId}",
        Body = "Notification body",
        DestinationPath = "/Reviews",
        IsWebVisible = visible,
        OccurredAt = createdAt,
        CreatedAt = createdAt,
        ExpiresAt = createdAt.AddDays(90),
    };

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
