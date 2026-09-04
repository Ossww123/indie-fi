using IndieFi.Web.Data;
using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Contracts;
using IndieFi.Web.Modules.Notifications.Domain;
using IndieFi.Web.Modules.Notifications.Infrastructure;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class NotificationMaintenanceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 16, 14, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlFixture _fixture;

    public NotificationMaintenanceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RunBatchAsync_AppliesRetentionWithoutDeletingPendingOrUnresolvedFailure()
    {
        await ResetAsync();
        var arranged = await ArrangeNotificationsAsync();
        await ArrangeMessagingStatesAsync();

        await using (var db = _fixture.NewDbContext())
        {
            var result = await new NotificationMaintenanceService(
                    db,
                    new FixedTimeProvider(Now))
                .RunBatchAsync(100, TestContext.Current.CancellationToken);

            Assert.Equal(3, result.ContentRemovedCount);
            Assert.Equal(1, result.DeliveryDeletedCount);
            Assert.Equal(1, result.OutboxStateDeletedCount);
            Assert.Equal(1, result.InboxStateDeletedCount);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var expired = await verificationDb.Notifications.SingleAsync(
            item => item.Id == arranged.ExpiredNotificationId,
            TestContext.Current.CancellationToken);
        Assert.Null(expired.Title);
        Assert.Null(expired.Body);
        Assert.Null(expired.DestinationPath);
        Assert.False(expired.IsWebVisible);
        Assert.Equal(Now, expired.ContentRemovedAt);
        Assert.False(await verificationDb.NotificationDeliveries.AnyAsync(
            item => item.Id == arranged.ResolvedDeliveryId,
            TestContext.Current.CancellationToken));
        Assert.True(await verificationDb.NotificationDeliveries.AnyAsync(
            item => item.Id == arranged.PendingDeliveryId
                && item.Status == NotificationDeliveryStatus.Pending,
            TestContext.Current.CancellationToken));
        Assert.True(await verificationDb.NotificationDeliveries.AnyAsync(
            item => item.Id == arranged.FailedDeliveryId
                && item.Status == NotificationDeliveryStatus.Failed,
            TestContext.Current.CancellationToken));
        Assert.NotNull(await verificationDb.Notifications
            .Where(item => item.Id == arranged.FreshNotificationId)
            .Select(item => item.Title)
            .SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await verificationDb.Set<OutboxState>().CountAsync(
            TestContext.Current.CancellationToken));
        Assert.Equal(1, await verificationDb.Set<InboxState>().CountAsync(
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Worker_RunsMaintenanceInBackground()
    {
        await ResetAsync();
        var arranged = await ArrangeNotificationsAsync();
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(_fixture.ConnectionString));
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
        services.AddScoped<NotificationMaintenanceService>();
        await using var provider = services.BuildServiceProvider();
        var worker = new NotificationMaintenanceWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<NotificationMaintenanceWorker>.Instance,
            enabled: true,
            interval: TimeSpan.FromMilliseconds(10),
            batchSize: 100);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitUntilAsync(async () =>
            {
                await using var db = _fixture.NewDbContext();
                return await db.Notifications.AnyAsync(
                    item => item.Id == arranged.ExpiredNotificationId
                        && item.ContentRemovedAt == Now,
                    TestContext.Current.CancellationToken);
            });
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Worker_WhenDisabled_DoesNotResolveMaintenanceService()
    {
        var services = new ServiceCollection();
        await using var provider = services.BuildServiceProvider();
        var worker = new NotificationMaintenanceWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<NotificationMaintenanceWorker>.Instance,
            enabled: false,
            interval: TimeSpan.FromMilliseconds(10),
            batchSize: 100);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await worker.StopAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5001)]
    public async Task RunBatchAsync_RejectsUnsafeBatchSize(int batchSize)
    {
        await using var db = _fixture.NewDbContext();
        var service = new NotificationMaintenanceService(
            db,
            new FixedTimeProvider(Now));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.RunBatchAsync(
                batchSize,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Options_RejectUnsafeEnabledSettingsAndAllowDisabledMode()
    {
        Assert.True(NotificationMaintenanceOptions.Validate(new()
        {
            Enabled = true,
            WorkerInterval = TimeSpan.FromMinutes(1),
            BatchSize = 5000,
        }).Succeeded);
        Assert.False(NotificationMaintenanceOptions.Validate(new()
        {
            Enabled = true,
            WorkerInterval = TimeSpan.FromSeconds(59),
            BatchSize = 100,
        }).Succeeded);
        Assert.True(NotificationMaintenanceOptions.Validate(new()
        {
            Enabled = false,
            WorkerInterval = TimeSpan.Zero,
            BatchSize = 0,
        }).Succeeded);
    }

    private async Task<ArrangedNotifications> ArrangeNotificationsAsync()
    {
        await using var db = _fixture.NewDbContext();
        var user = TestAccountProtection.AddUser(
            db,
            $"maintenance-{Guid.NewGuid():N}@example.test");
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var resolved = CreateNotification(user.Id, Now.AddDays(-121));
        var resolvedDelivery = CreateDelivery(
            resolved.CreatedAt,
            NotificationDeliveryStatus.Delivered);
        resolved.Deliveries.Add(resolvedDelivery);
        var pending = CreateNotification(user.Id, Now.AddDays(-121));
        var pendingDelivery = CreateDelivery(
            pending.CreatedAt,
            NotificationDeliveryStatus.Pending);
        pending.Deliveries.Add(pendingDelivery);
        var failed = CreateNotification(user.Id, Now.AddDays(-121));
        var failedDelivery = CreateDelivery(
            failed.CreatedAt,
            NotificationDeliveryStatus.Failed);
        failed.Deliveries.Add(failedDelivery);
        var fresh = CreateNotification(user.Id, Now.AddDays(-10));
        fresh.Deliveries.Add(CreateDelivery(
            fresh.CreatedAt,
            NotificationDeliveryStatus.Delivered));
        db.Notifications.AddRange(resolved, pending, failed, fresh);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return new(
            resolved.Id,
            fresh.Id,
            resolvedDelivery.Id,
            pendingDelivery.Id,
            failedDelivery.Id);
    }

    private async Task ArrangeMessagingStatesAsync()
    {
        await using var db = _fixture.NewOwnerDbContext();
        var oldOutbox = CreateOutboxState(Now.AddDays(-8));
        var newOutbox = CreateOutboxState(Now.AddDays(-6));
        var oldInbox = CreateInboxState(Now.AddDays(-31), Now.AddMinutes(-1));
        var newInbox = CreateInboxState(Now.AddDays(-29), Now.AddDays(1));
        db.AddRange(oldOutbox, newOutbox, oldInbox, newInbox);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static Notification CreateNotification(
        long userId,
        DateTimeOffset createdAt) => new()
    {
        UserId = userId,
        EventId = Guid.NewGuid(),
        Kind = NotificationKind.AccountStatusChanged,
        SourceType = NotificationSourceTypes.User,
        SourceId = userId,
        Title = "Maintenance notification",
        Body = "Notification body.",
        DestinationPath = "/Account/Profile",
        IsWebVisible = true,
        OccurredAt = createdAt,
        CreatedAt = createdAt,
        ExpiresAt = createdAt.Add(NotificationPolicy.WebRetention),
    };

    private static NotificationDelivery CreateDelivery(
        DateTimeOffset createdAt,
        NotificationDeliveryStatus status) => new()
    {
        MessageId = Guid.NewGuid(),
        Channel = NotificationChannel.Email,
        Status = status,
        AttemptCount = status == NotificationDeliveryStatus.Pending ? 0 : 1,
        CreatedAt = createdAt,
        NextAttemptAt = status == NotificationDeliveryStatus.Pending ? Now : null,
        LastAttemptAt = status == NotificationDeliveryStatus.Pending
            ? null
            : createdAt.AddMinutes(1),
        DeliveredAt = status == NotificationDeliveryStatus.Delivered
            ? createdAt.AddMinutes(1)
            : null,
        FailedAt = status == NotificationDeliveryStatus.Failed
            ? createdAt.AddMinutes(1)
            : null,
        LastErrorCode = status == NotificationDeliveryStatus.Failed
            ? "smtp_mailbox_unavailable"
            : null,
    };

    private static OutboxState CreateOutboxState(DateTimeOffset deliveredAt) => new()
    {
        OutboxId = Guid.NewGuid(),
        LockId = Guid.NewGuid(),
        Created = deliveredAt.AddMinutes(-1).UtcDateTime,
        Delivered = deliveredAt.UtcDateTime,
    };

    private static InboxState CreateInboxState(
        DateTimeOffset consumedAt,
        DateTimeOffset expirationTime) => new()
    {
        MessageId = Guid.NewGuid(),
        ConsumerId = Guid.NewGuid(),
        LockId = Guid.NewGuid(),
        Received = consumedAt.AddMinutes(-1).UtcDateTime,
        ReceiveCount = 1,
        Consumed = consumedAt.UtcDateTime,
        Delivered = consumedAt.UtcDateTime,
        ExpirationTime = expirationTime.UtcDateTime,
    };

    private async Task ResetAsync()
    {
        await using var db = _fixture.NewOwnerDbContext();
        await db.Set<MassTransit.EntityFrameworkCoreIntegration.OutboxMessage>()
            .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await db.Set<InboxState>()
            .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await db.Set<OutboxState>()
            .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await db.Notifications.ExecuteDeleteAsync(
            TestContext.Current.CancellationToken);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < expiresAt)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(25),
                TestContext.Current.CancellationToken);
        }

        Assert.Fail("The notification maintenance Worker did not run in time.");
    }

    private sealed record ArrangedNotifications(
        long ExpiredNotificationId,
        long FreshNotificationId,
        long ResolvedDeliveryId,
        long PendingDeliveryId,
        long FailedDeliveryId);

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
