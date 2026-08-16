using System.Collections.Concurrent;
using IndieFi.Web.Data;
using IndieFi.Web.Infrastructure.Realtime;
using IndieFi.Web.Modules.Notifications;
using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Contracts;
using IndieFi.Web.Modules.Notifications.Domain;
using IndieFi.Web.Modules.Notifications.Infrastructure;
using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Testcontainers.RabbitMq;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class RabbitMqOutboxRecoveryTests : IAsyncLifetime
{
    private const string RabbitUsername = "indiefi_test";
    private const string RabbitPassword = "rabbit-test-password";
    private readonly PostgreSqlFixture _postgres;
    private readonly RabbitMqContainer _rabbitMq =
        new RabbitMqBuilder("rabbitmq:4.3.4-management-alpine")
            .WithUsername(RabbitUsername)
            .WithPassword(RabbitPassword)
            .Build();

    public RabbitMqOutboxRecoveryTests(PostgreSqlFixture postgres)
    {
        _postgres = postgres;
    }

    public async ValueTask InitializeAsync()
    {
        await _rabbitMq.StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RabbitMq_RedrivesOutboxAfterOutage_AndInboxAbsorbsDuplicate()
    {
        await ClearMessagingStateAsync();
        var settings = BuildMessagingSettings();
        var recorder = new ProbeDeliveryRecorder();
        using var consumerHost = BuildHost(settings, recorder, addConsumer: true);
        using var producerHost = BuildHost(settings, recorder, addConsumer: false);

        await consumerHost.StartAsync(TestContext.Current.CancellationToken);
        await producerHost.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var firstEventId = Guid.NewGuid();
            await StageAsync(producerHost.Services, firstEventId, sourceId: 101);
            await WaitUntilAsync(
                () => recorder.Count(firstEventId) == 1,
                TimeSpan.FromSeconds(30));

            await StageAsync(producerHost.Services, firstEventId, sourceId: 101);
            await WaitUntilOutboxIsEmptyAsync(firstEventId, TimeSpan.FromSeconds(30));
            await Task.Delay(TimeSpan.FromMilliseconds(500),
                TestContext.Current.CancellationToken);
            Assert.Equal(1, recorder.Count(firstEventId));

            await using (var db = _postgres.NewDbContext())
            {
                var inboxStates = await db.Set<InboxState>()
                    .Where(item => item.MessageId == firstEventId)
                    .ToListAsync(TestContext.Current.CancellationToken);
                Assert.Equal(2, inboxStates.Count);
                Assert.All(inboxStates,
                    inbox => Assert.True(inbox.ReceiveCount >= 2));
            }

            var stopResult = await _rabbitMq.ExecAsync(
                ["rabbitmqctl", "stop_app"],
                TestContext.Current.CancellationToken);
            Assert.Equal(0, stopResult.ExitCode);
            var outageEventId = Guid.NewGuid();
            await StageAsync(producerHost.Services, outageEventId, sourceId: 202);

            await using (var db = _postgres.NewDbContext())
            {
                Assert.True(await db.Set<OutboxMessage>().AnyAsync(
                    item => item.MessageId == outageEventId,
                    TestContext.Current.CancellationToken));
            }

            var startResult = await _rabbitMq.ExecAsync(
                ["rabbitmqctl", "start_app"],
                TestContext.Current.CancellationToken);
            Assert.Equal(0, startResult.ExitCode);
            await WaitUntilAsync(
                () => recorder.Count(outageEventId) == 1,
                TimeSpan.FromSeconds(60));
            await WaitUntilOutboxIsEmptyAsync(
                outageEventId,
                TimeSpan.FromSeconds(30));
        }
        finally
        {
            await producerHost.StopAsync(CancellationToken.None);
            await consumerHost.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RabbitMq_WebOutboxToWorker_CreatesNotificationAndChannelDeliveries()
    {
        await ClearMessagingStateAsync();
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_postgres);
        var settings = BuildMessagingSettings();
        var recorder = new ProbeDeliveryRecorder();
        using var consumerHost = BuildHost(settings, recorder, addConsumer: true);
        using var producerHost = BuildHost(settings, recorder, addConsumer: false);

        await consumerHost.StartAsync(TestContext.Current.CancellationToken);
        await producerHost.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var eventId = Guid.NewGuid();
            await StageAsync(
                producerHost.Services,
                eventId,
                assignment.AssignmentId,
                assignment.ReviewerId);
            await WaitUntilAsync(async () =>
            {
                await using var db = _postgres.NewDbContext();
                return await db.Notifications.AnyAsync(
                    item => item.EventId == eventId
                        && item.UserId == assignment.ReviewerId,
                    TestContext.Current.CancellationToken);
            }, TimeSpan.FromSeconds(30));

            await using var verificationDb = _postgres.NewDbContext();
            var notification = await verificationDb.Notifications
                .Include(item => item.Deliveries)
                .SingleAsync(
                    item => item.EventId == eventId,
                    TestContext.Current.CancellationToken);
            Assert.True(notification.IsWebVisible);
            Assert.Equal(2, notification.Deliveries.Count);
            Assert.Contains(notification.Deliveries,
                item => item.Channel == NotificationChannel.Web
                    && item.Status == NotificationDeliveryStatus.Delivered);
            Assert.Contains(notification.Deliveries,
                item => item.Channel == NotificationChannel.Email
                    && item.Status == NotificationDeliveryStatus.Pending);
        }
        finally
        {
            await producerHost.StopAsync(CancellationToken.None);
            await consumerHost.StopAsync(CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _rabbitMq.DisposeAsync();
    }

    private IHost BuildHost(
        IReadOnlyDictionary<string, string?> settings,
        ProbeDeliveryRecorder recorder,
        bool addConsumer)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddSingleton(recorder);
        builder.Services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(_postgres.ConnectionString));
        builder.Services.AddIndieFiRealtime(builder.Configuration);
        builder.Services.AddNotificationsModule(
            builder.Configuration,
            registration =>
            {
                if (addConsumer)
                {
                    registration.AddConsumer<RabbitMqProbeConsumer>();
                    registration.AddNotificationConsumers();
                }
            });
        return builder.Build();
    }

    private IReadOnlyDictionary<string, string?> BuildMessagingSettings()
    {
        var connection = new Uri(_rabbitMq.GetConnectionString());
        return new Dictionary<string, string?>
        {
            ["Messaging:RabbitMq:Enabled"] = "true",
            ["Messaging:RabbitMq:Host"] = connection.Host,
            ["Messaging:RabbitMq:Port"] = connection.Port.ToString(),
            ["Messaging:RabbitMq:VirtualHost"] = "/",
            ["Messaging:RabbitMq:Username"] = RabbitUsername,
            ["Messaging:RabbitMq:Password"] = RabbitPassword,
            ["Messaging:RabbitMq:EndpointPrefix"] =
                $"indiefi-test-{Guid.NewGuid():N}"[..45],
        };
    }

    private static async Task StageAsync(
        IServiceProvider services,
        Guid eventId,
        long sourceId,
        long recipientUserId = 1)
    {
        await using var scope = services.CreateAsyncScope();
        var publisher = scope.ServiceProvider
            .GetRequiredService<INotificationEventPublisher>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await publisher.StageAsync(
            eventId,
            recipientUserId,
            NotificationKind.ReviewAssigned,
            NotificationSourceTypes.ReviewAssignment,
            sourceId,
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task ClearMessagingStateAsync()
    {
        await using var db = _postgres.NewDbContext();
        await db.Set<OutboxMessage>().ExecuteDeleteAsync(
            TestContext.Current.CancellationToken);
        await db.Set<InboxState>().ExecuteDeleteAsync(
            TestContext.Current.CancellationToken);
        await db.Set<OutboxState>().ExecuteDeleteAsync(
            TestContext.Current.CancellationToken);
    }

    private async Task WaitUntilOutboxIsEmptyAsync(
        Guid eventId,
        TimeSpan timeout)
    {
        await WaitUntilAsync(async () =>
        {
            await using var db = _postgres.NewDbContext();
            return !await db.Set<OutboxMessage>().AnyAsync(
                item => item.MessageId == eventId,
                TestContext.Current.CancellationToken);
        }, timeout);
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        await WaitUntilAsync(() => Task.FromResult(condition()), timeout);
    }

    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout)
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < expiresAt)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(100),
                TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Condition was not met within {timeout}.");
    }
}

public sealed class RabbitMqProbeConsumer :
    IConsumer<UserNotificationRequestedV1>
{
    private readonly ProbeDeliveryRecorder _recorder;

    public RabbitMqProbeConsumer(ProbeDeliveryRecorder recorder)
    {
        _recorder = recorder;
    }

    public Task Consume(ConsumeContext<UserNotificationRequestedV1> context)
    {
        _recorder.Record(context.Message.EventId);
        return Task.CompletedTask;
    }
}

public sealed class ProbeDeliveryRecorder
{
    private readonly ConcurrentDictionary<Guid, int> _counts = new();

    public void Record(Guid eventId) =>
        _counts.AddOrUpdate(eventId, 1, (_, count) => count + 1);

    public int Count(Guid eventId) =>
        _counts.GetValueOrDefault(eventId);
}
