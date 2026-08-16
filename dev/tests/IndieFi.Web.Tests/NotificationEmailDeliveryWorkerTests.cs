using IndieFi.Web.Data;
using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Contracts;
using IndieFi.Web.Modules.Notifications.Domain;
using IndieFi.Web.Modules.Notifications.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class NotificationEmailDeliveryWorkerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 16, 10, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlFixture _fixture;

    public NotificationEmailDeliveryWorkerTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Worker_ProcessesPendingDeliveryInBackground()
    {
        await ResetNotificationsAsync();
        var deliveryId = await ArrangeDeliveryAsync();
        var sender = new RecordingSender();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(_fixture.ConnectionString));
        services.AddSingleton(TestAccountProtection.CreateService());
        services.AddSingleton(new NotificationEmailTemplateRenderer(
            Microsoft.Extensions.Options.Options.Create(
                new NotificationEmailDeliveryOptions
                {
                    PublicBaseUrl = "https://indiefi.example/",
                })));
        services.AddSingleton<INotificationEmailSender>(sender);
        services.AddScoped<NotificationEmailDeliveryAuthorityService>();
        services.AddScoped<NotificationEmailDeliveryProcessor>();
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
        await using var provider = services.BuildServiceProvider();
        var worker = new NotificationEmailDeliveryWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<NotificationEmailDeliveryWorker>.Instance,
            enabled: true,
            interval: TimeSpan.FromMilliseconds(10),
            batchSize: 10);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitUntilAsync(async () =>
            {
                await using var db = _fixture.NewDbContext();
                return await db.NotificationDeliveries.AnyAsync(
                    item => item.Id == deliveryId
                        && item.Status == NotificationDeliveryStatus.Delivered,
                    TestContext.Current.CancellationToken);
            });
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.Single(sender.Requests);
    }

    [Fact]
    public async Task Worker_WhenDisabled_DoesNotResolveProcessor()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        var worker = new NotificationEmailDeliveryWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<NotificationEmailDeliveryWorker>.Instance,
            enabled: false,
            interval: TimeSpan.FromMilliseconds(10),
            batchSize: 10);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await worker.StopAsync(TestContext.Current.CancellationToken);
    }

    private async Task<long> ArrangeDeliveryAsync()
    {
        await using var db = _fixture.NewDbContext();
        var user = TestAccountProtection.AddUser(db, "worker@example.test");
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var notification = new Notification
        {
            UserId = user.Id,
            EventId = Guid.NewGuid(),
            Kind = NotificationKind.AccountStatusChanged,
            SourceType = NotificationSourceTypes.User,
            SourceId = user.Id,
            Title = "Account status",
            Body = "Your account status changed.",
            DestinationPath = "/Account/Profile",
            IsWebVisible = true,
            OccurredAt = Now.AddMinutes(-1),
            CreatedAt = Now,
            ExpiresAt = Now.AddDays(90),
        };
        var delivery = new NotificationDelivery
        {
            MessageId = Guid.NewGuid(),
            Channel = NotificationChannel.Email,
            Status = NotificationDeliveryStatus.Pending,
            CreatedAt = Now,
            NextAttemptAt = Now,
        };
        notification.Deliveries.Add(delivery);
        db.Notifications.Add(notification);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return delivery.Id;
    }

    private async Task ResetNotificationsAsync()
    {
        await using var db = _fixture.NewOwnerDbContext();
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

        Assert.Fail("The notification email Worker did not process the delivery.");
    }

    private sealed class RecordingSender : INotificationEmailSender
    {
        public List<NotificationEmailSendRequest> Requests { get; } = [];

        public Task SendAsync(
            NotificationEmailSendRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

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
