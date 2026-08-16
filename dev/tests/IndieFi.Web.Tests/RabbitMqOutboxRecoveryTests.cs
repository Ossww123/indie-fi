using System.Collections.Concurrent;
using IndieFi.Web.Data;
using IndieFi.Web.Infrastructure.Realtime;
using IndieFi.Web.Modules.Feedback.Application;
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

    [Fact]
    public async Task RabbitMq_FeedbackProducers_ProjectFourDomainNotifications()
    {
        await ClearMessagingStateAsync();
        long requesterId;
        long reviewerId;
        long firstRequestId;
        await using (var arrangeDb = _postgres.NewDbContext())
        {
            await arrangeDb.ReviewRequests.ExecuteDeleteAsync(
                TestContext.Current.CancellationToken);
            requesterId = (await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 2)).Id;
            reviewerId = (await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0)).Id;
            var game = await ReviewTestData.CreateGameAsync(arrangeDb, requesterId);
            firstRequestId = (await ReviewTestData.CreateQueuedRequestAsync(
                arrangeDb,
                requesterId,
                game.Id)).Id;
        }

        var settings = BuildMessagingSettings();
        var recorder = new ProbeDeliveryRecorder();
        using var consumerHost = BuildHost(settings, recorder, addConsumer: true);
        using var producerHost = BuildHost(settings, recorder, addConsumer: false);
        await consumerHost.StartAsync(TestContext.Current.CancellationToken);
        await producerHost.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var firstOperationId = Guid.NewGuid();
            await using (var scope = producerHost.Services.CreateAsyncScope())
            {
                var claim = scope.ServiceProvider.GetRequiredService<ReviewClaimService>();
                var result = await claim.ClaimNextAsync(reviewerId, firstOperationId);
                Assert.Equal(ReviewClaimOutcome.Claimed, result.Outcome);
                Assert.Equal(firstRequestId, result.RequestId);
            }

            long firstAssignmentId;
            await using (var db = _postgres.NewDbContext())
            {
                firstAssignmentId = await db.ReviewAssignments
                    .Where(item => item.ReviewerId == reviewerId
                        && item.ClaimOperationId == firstOperationId)
                    .Select(item => item.Id)
                    .SingleAsync(TestContext.Current.CancellationToken);
            }

            var assignedEventId = NotificationEventId.Create(
                NotificationKind.ReviewAssigned,
                firstAssignmentId,
                reviewerId);
            await WaitForNotificationAsync(assignedEventId, reviewerId);

            await using (var db = _postgres.NewDbContext())
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE review_assignments
                       SET leased_until = {DateTimeOffset.UtcNow.AddMinutes(5)},
                           deadline_notification_staged_at = NULL
                     WHERE id = {firstAssignmentId}
                    """,
                    TestContext.Current.CancellationToken);
            }

            await using (var scope = producerHost.Services.CreateAsyncScope())
            {
                var deadlines = scope.ServiceProvider
                    .GetRequiredService<ReviewDeadlineNotificationService>();
                Assert.Equal(
                    1,
                    (await deadlines.StageDueAsync(
                        cancellationToken: TestContext.Current.CancellationToken))
                    .StagedCount);
            }

            var deadlineEventId = NotificationEventId.Create(
                NotificationKind.ReviewDeadlineSoon,
                firstAssignmentId,
                reviewerId);
            await WaitForNotificationAsync(deadlineEventId, reviewerId);

            await using (var scope = producerHost.Services.CreateAsyncScope())
            {
                var submissions = scope.ServiceProvider
                    .GetRequiredService<FeedbackSubmissionService>();
                var result = await submissions.SubmitAsync(
                    firstAssignmentId,
                    reviewerId,
                    "The real RabbitMQ path should notify the game owner safely.");
                Assert.Equal(FeedbackSubmissionOutcome.Submitted, result.Outcome);
            }

            long feedbackId;
            await using (var db = _postgres.NewDbContext())
            {
                feedbackId = await db.FeedbackSubmissions
                    .Where(item => item.AssignmentId == firstAssignmentId)
                    .Select(item => item.Id)
                    .SingleAsync(TestContext.Current.CancellationToken);
            }

            var feedbackEventId = NotificationEventId.Create(
                NotificationKind.FeedbackCompleted,
                feedbackId,
                requesterId);
            await WaitForNotificationAsync(feedbackEventId, requesterId);

            long secondRequestId;
            await using (var arrangeDb = _postgres.NewDbContext())
            {
                var game = await ReviewTestData.CreateGameAsync(arrangeDb, requesterId);
                secondRequestId = (await ReviewTestData.CreateQueuedRequestAsync(
                    arrangeDb,
                    requesterId,
                    game.Id)).Id;
            }

            var secondOperationId = Guid.NewGuid();
            await using (var scope = producerHost.Services.CreateAsyncScope())
            {
                var claim = scope.ServiceProvider.GetRequiredService<ReviewClaimService>();
                var result = await claim.ClaimNextAsync(reviewerId, secondOperationId);
                Assert.Equal(ReviewClaimOutcome.Claimed, result.Outcome);
                Assert.Equal(secondRequestId, result.RequestId);
            }

            long secondAssignmentId;
            await using (var db = _postgres.NewDbContext())
            {
                secondAssignmentId = await db.ReviewAssignments
                    .Where(item => item.ReviewerId == reviewerId
                        && item.ClaimOperationId == secondOperationId)
                    .Select(item => item.Id)
                    .SingleAsync(TestContext.Current.CancellationToken);
            }

            await WaitForNotificationAsync(
                NotificationEventId.Create(
                    NotificationKind.ReviewAssigned,
                    secondAssignmentId,
                    reviewerId),
                reviewerId);
            await using (var db = _postgres.NewDbContext())
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE review_assignments
                       SET leased_until = {DateTimeOffset.UtcNow.AddMinutes(-1)}
                     WHERE id = {secondAssignmentId}
                    """,
                    TestContext.Current.CancellationToken);
            }

            await using (var scope = producerHost.Services.CreateAsyncScope())
            {
                var sweep = scope.ServiceProvider
                    .GetRequiredService<ReviewLeaseSweepService>();
                Assert.Equal(1, (await sweep.SweepAsync()).ExpiredCount);
            }

            var expiredEventId = NotificationEventId.Create(
                NotificationKind.ReviewExpired,
                secondAssignmentId,
                reviewerId);
            await WaitForNotificationAsync(expiredEventId, reviewerId);

            await using var verificationDb = _postgres.NewDbContext();
            var notifications = await verificationDb.Notifications
                .Where(item => item.EventId == assignedEventId
                    || item.EventId == deadlineEventId
                    || item.EventId == feedbackEventId
                    || item.EventId == expiredEventId)
                .OrderBy(item => item.Kind)
                .ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(4, notifications.Count);
            Assert.Contains(notifications,
                item => item.Kind == NotificationKind.ReviewAssigned
                    && item.UserId == reviewerId);
            Assert.Contains(notifications,
                item => item.Kind == NotificationKind.ReviewDeadlineSoon
                    && item.UserId == reviewerId);
            Assert.Contains(notifications,
                item => item.Kind == NotificationKind.FeedbackCompleted
                    && item.UserId == requesterId);
            Assert.Contains(notifications,
                item => item.Kind == NotificationKind.ReviewExpired
                    && item.UserId == reviewerId);
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
        builder.Services.AddScoped<ReviewClaimService>();
        builder.Services.AddScoped<FeedbackSubmissionService>();
        builder.Services.AddScoped<ReviewDeadlineNotificationService>();
        builder.Services.AddScoped<ReviewLeaseSweepService>();
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

    private Task WaitForNotificationAsync(Guid eventId, long userId) =>
        WaitUntilAsync(async () =>
        {
            await using var db = _postgres.NewDbContext();
            return await db.Notifications.AnyAsync(
                item => item.EventId == eventId && item.UserId == userId,
                TestContext.Current.CancellationToken);
        }, TimeSpan.FromSeconds(30));

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
