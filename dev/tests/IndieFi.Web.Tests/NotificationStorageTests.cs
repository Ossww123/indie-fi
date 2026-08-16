using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class NotificationStorageTests
{
    private readonly PostgreSqlFixture _fixture;

    public NotificationStorageTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Save_ValidNotificationAndDeliveries_PersistsPolicySnapshot()
    {
        var userId = await CreateUserAsync();
        var notification = CreateNotification(userId);
        notification.Deliveries.Add(CreateDelivery(NotificationChannel.Web, false));
        notification.Deliveries.Add(CreateDelivery(NotificationChannel.Email, true));

        await using (var db = _fixture.NewDbContext())
        {
            db.Notifications.Add(notification);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var saved = await verificationDb.Notifications
            .Include(item => item.Deliveries)
            .SingleAsync(
                item => item.Id == notification.Id,
                TestContext.Current.CancellationToken);
        Assert.True(saved.IsWebVisible);
        Assert.Equal(2, saved.Deliveries.Count);
        Assert.Contains(
            saved.Deliveries,
            item => item.Channel == NotificationChannel.Web
                && item.Status == NotificationDeliveryStatus.Delivered);
        Assert.Contains(
            saved.Deliveries,
            item => item.Channel == NotificationChannel.Email
                && item.Status == NotificationDeliveryStatus.Pending);
    }

    [Fact]
    public async Task Save_DuplicateRecipientEvent_IsRejected()
    {
        var userId = await CreateUserAsync();
        var first = CreateNotification(userId);
        await using (var db = _fixture.NewDbContext())
        {
            db.Notifications.Add(first);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var duplicateDb = _fixture.NewDbContext();
        duplicateDb.Notifications.Add(CreateNotification(userId, first.EventId));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => duplicateDb.SaveChangesAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            PostgresErrorCodes.UniqueViolation,
            Assert.IsType<PostgresException>(exception.InnerException).SqlState);
    }

    [Fact]
    public async Task Save_DuplicateChannelDelivery_IsRejected()
    {
        var userId = await CreateUserAsync();
        var notification = CreateNotification(userId);
        notification.Deliveries.Add(CreateDelivery(NotificationChannel.Web, false));
        notification.Deliveries.Add(CreateDelivery(NotificationChannel.Web, false));

        await using var db = _fixture.NewDbContext();
        db.Notifications.Add(notification);
        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            PostgresErrorCodes.UniqueViolation,
            Assert.IsType<PostgresException>(exception.InnerException).SqlState);
    }

    [Fact]
    public async Task Save_DisabledMandatoryPreference_IsRejected()
    {
        var userId = await CreateUserAsync();
        await using var db = _fixture.NewDbContext();
        db.NotificationPreferences.Add(new NotificationPreference
        {
            UserId = userId,
            Kind = NotificationKind.AccountStatusChanged,
            WebEnabled = true,
            EmailEnabled = false,
        });

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));
        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.CheckViolation, postgres.SqlState);
        Assert.Equal("CK_notification_preferences_mandatory", postgres.ConstraintName);
    }

    [Fact]
    public async Task Save_DeliveryWithImpossibleState_IsRejected()
    {
        var userId = await CreateUserAsync();
        var notification = CreateNotification(userId);
        notification.Deliveries.Add(new NotificationDelivery
        {
            MessageId = Guid.NewGuid(),
            Channel = NotificationChannel.Email,
            Status = NotificationDeliveryStatus.Delivered,
        });

        await using var db = _fixture.NewDbContext();
        db.Notifications.Add(notification);
        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));

        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.CheckViolation, postgres.SqlState);
        Assert.Equal(
            "CK_notification_deliveries_state_shape",
            postgres.ConstraintName);
    }

    [Fact]
    public async Task Save_DuplicateKeyDropSubscription_IsRejected()
    {
        var (userId, dropId) = await CreateUserAndDropAsync();
        await using (var db = _fixture.NewDbContext())
        {
            db.KeyDropSubscriptions.Add(new KeyDropSubscription
            {
                UserId = userId,
                DropId = dropId,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var duplicateDb = _fixture.NewDbContext();
        duplicateDb.KeyDropSubscriptions.Add(new KeyDropSubscription
        {
            UserId = userId,
            DropId = dropId,
        });
        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => duplicateDb.SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            PostgresErrorCodes.UniqueViolation,
            Assert.IsType<PostgresException>(exception.InnerException).SqlState);
    }

    [Fact]
    public async Task ApplicationRole_CannotRewriteNotificationIdentityOrDeleteHistory()
    {
        var userId = await CreateUserAsync();
        var notification = CreateNotification(userId);
        await using (var db = _fixture.NewDbContext())
        {
            db.Notifications.Add(notification);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var mutationDb = _fixture.NewDbContext();
        var updateException = await Assert.ThrowsAsync<PostgresException>(
            () => mutationDb.Notifications
                .Where(item => item.Id == notification.Id)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(item => item.EventId, Guid.NewGuid()),
                    TestContext.Current.CancellationToken));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, updateException.SqlState);

        var deleteException = await Assert.ThrowsAsync<PostgresException>(
            () => mutationDb.Notifications
                .Where(item => item.Id == notification.Id)
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, deleteException.SqlState);
    }

    private async Task<long> CreateUserAsync()
    {
        await using var db = _fixture.NewDbContext();
        var user = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        return user.Id;
    }

    private async Task<(long UserId, long DropId)> CreateUserAndDropAsync()
    {
        await using var db = _fixture.NewDbContext();
        var user = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
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
        return (user.Id, drop.Id);
    }

    private static Notification CreateNotification(long userId, Guid? eventId = null)
    {
        var createdAt = DateTimeOffset.UtcNow;
        return new Notification
        {
            UserId = userId,
            EventId = eventId ?? Guid.NewGuid(),
            Kind = NotificationKind.ReviewAssigned,
            SourceType = "ReviewAssignment",
            SourceId = 1,
            Title = "새 리뷰 작업이 배정됐습니다.",
            Body = "마감 시각 전에 리뷰를 제출해 주세요.",
            DestinationPath = "/Reviews",
            IsWebVisible = true,
            OccurredAt = createdAt.AddSeconds(-1),
            CreatedAt = createdAt,
            ExpiresAt = createdAt.Add(NotificationPolicy.WebRetention),
        };
    }

    private static NotificationDelivery CreateDelivery(
        NotificationChannel channel,
        bool pending)
    {
        var now = DateTimeOffset.UtcNow;
        return new NotificationDelivery
        {
            MessageId = Guid.NewGuid(),
            Channel = channel,
            Status = pending
                ? NotificationDeliveryStatus.Pending
                : NotificationDeliveryStatus.Delivered,
            CreatedAt = now,
            NextAttemptAt = pending ? now.AddMinutes(1) : null,
            DeliveredAt = pending ? null : now,
        };
    }
}
