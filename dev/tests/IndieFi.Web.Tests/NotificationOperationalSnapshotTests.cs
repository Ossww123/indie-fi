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
public sealed class NotificationOperationalSnapshotTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 16, 15, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlFixture _fixture;

    public NotificationOperationalSnapshotTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetAsync_ReadsOutboxAndEmailBacklogWithoutPayloadData()
    {
        await ResetAsync();
        await ArrangeBacklogAsync();

        await using var db = _fixture.NewDbContext();
        var service = new NotificationOperationalSnapshotService(
            db,
            new NotificationEmailOperationsService(db));
        var snapshot = await service.GetAsync(
            Now,
            rabbitMqReady: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, snapshot.OutboxBacklogCount);
        Assert.Equal(300, snapshot.OldestOutboxAgeSeconds);
        Assert.Equal(1, snapshot.EmailPendingCount);
        Assert.Equal(1, snapshot.EmailDueCount);
        Assert.Equal(1, snapshot.EmailFailedCount);
        Assert.Equal(120, snapshot.OldestEmailDueAgeSeconds);
        Assert.True(snapshot.RabbitMqReady);
    }

    [Fact]
    public async Task Worker_UpdatesOperationalGaugesWithTransportHealth()
    {
        await ResetAsync();
        await ArrangeBacklogAsync();
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(_fixture.ConnectionString));
        services.AddScoped<NotificationEmailOperationsService>();
        services.AddScoped<NotificationOperationalSnapshotService>();
        await using var provider = services.BuildServiceProvider();
        var worker = new NotificationOperationsWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FixedTransportHealth(isReady: false),
            new FixedTimeProvider(Now),
            NullLogger<NotificationOperationsWorker>.Instance,
            enabled: true,
            interval: TimeSpan.FromMilliseconds(10));
        var observed = new Dictionary<string, double>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == NotificationTelemetry.MeterName
                && instrument.Name is "indiefi.notifications.outbox.backlog"
                    or "indiefi.notifications.rabbitmq.ready")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
            observed[instrument.Name] = value);
        listener.Start();

        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitUntilAsync(() =>
            {
                listener.RecordObservableInstruments();
                return observed.TryGetValue(
                        "indiefi.notifications.outbox.backlog",
                        out var backlog)
                    && backlog == 1
                    && observed.TryGetValue(
                        "indiefi.notifications.rabbitmq.ready",
                        out var ready)
                    && ready == 0;
            });
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void Options_BoundObservationIntervalAndAllowDisabledMode()
    {
        Assert.True(NotificationOperationsOptions.Validate(new()
        {
            Enabled = true,
            ObservationInterval = TimeSpan.FromSeconds(5),
        }).Succeeded);
        Assert.False(NotificationOperationsOptions.Validate(new()
        {
            Enabled = true,
            ObservationInterval = TimeSpan.FromSeconds(4),
        }).Succeeded);
        Assert.True(NotificationOperationsOptions.Validate(new()
        {
            Enabled = false,
            ObservationInterval = TimeSpan.Zero,
        }).Succeeded);
    }

    private async Task ArrangeBacklogAsync()
    {
        await using var db = _fixture.NewOwnerDbContext();
        var user = TestAccountProtection.AddUser(
            db,
            $"operations-{Guid.NewGuid():N}@example.test");
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var pending = CreateNotification(user.Id);
        pending.Deliveries.Add(new NotificationDelivery
        {
            MessageId = Guid.NewGuid(),
            Channel = NotificationChannel.Email,
            Status = NotificationDeliveryStatus.Pending,
            CreatedAt = Now.AddMinutes(-3),
            NextAttemptAt = Now.AddMinutes(-2),
        });
        var failed = CreateNotification(user.Id);
        failed.Deliveries.Add(new NotificationDelivery
        {
            MessageId = Guid.NewGuid(),
            Channel = NotificationChannel.Email,
            Status = NotificationDeliveryStatus.Failed,
            AttemptCount = 1,
            CreatedAt = Now.AddMinutes(-3),
            LastAttemptAt = Now.AddMinutes(-2),
            FailedAt = Now.AddMinutes(-2),
            LastErrorCode = "smtp_mailbox_unavailable",
        });
        db.Notifications.AddRange(pending, failed);
        db.Set<OutboxMessage>().Add(new OutboxMessage
        {
            MessageId = Guid.NewGuid(),
            ContentType = "application/vnd.masstransit+json",
            MessageType = "urn:message:IndieFi:Probe",
            Body = "{}",
            SentTime = Now.AddMinutes(-5).UtcDateTime,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static Notification CreateNotification(long userId) => new()
    {
        UserId = userId,
        EventId = Guid.NewGuid(),
        Kind = NotificationKind.AccountStatusChanged,
        SourceType = NotificationSourceTypes.User,
        SourceId = userId,
        Title = "Operations notification",
        Body = "Notification body.",
        DestinationPath = "/Account/Profile",
        IsWebVisible = true,
        OccurredAt = Now.AddMinutes(-3),
        CreatedAt = Now.AddMinutes(-3),
        ExpiresAt = Now.AddDays(90),
    };

    private async Task ResetAsync()
    {
        await using var db = _fixture.NewOwnerDbContext();
        await db.Set<OutboxMessage>().ExecuteDeleteAsync(
            TestContext.Current.CancellationToken);
        await db.Set<InboxState>().ExecuteDeleteAsync(
            TestContext.Current.CancellationToken);
        await db.Set<OutboxState>().ExecuteDeleteAsync(
            TestContext.Current.CancellationToken);
        await db.Notifications.ExecuteDeleteAsync(
            TestContext.Current.CancellationToken);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < expiresAt)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(25),
                TestContext.Current.CancellationToken);
        }

        Assert.Fail("The notification operations Worker did not update gauges.");
    }

    private sealed class FixedTransportHealth : INotificationTransportHealth
    {
        private readonly bool _isReady;

        public FixedTransportHealth(bool isReady)
        {
            _isReady = isReady;
        }

        public bool IsReady() => _isReady;
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
