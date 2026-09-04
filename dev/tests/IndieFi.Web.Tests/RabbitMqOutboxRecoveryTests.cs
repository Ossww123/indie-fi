using System.Collections.Concurrent;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Infrastructure.Realtime;
using IndieFi.Web.Modules.Feedback.Application;
using IndieFi.Web.Modules.Games.Application;
using IndieFi.Web.Modules.KeyDistributions.Application;
using IndieFi.Web.Modules.Notifications;
using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Contracts;
using IndieFi.Web.Modules.Notifications.Domain;
using IndieFi.Web.Modules.Notifications.Infrastructure;
using IndieFi.Web.Modules.Profiles.Domain;
using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class RabbitMqOutboxRecoveryTests : IClassFixture<RabbitMqFixture>
{
    private readonly PostgreSqlFixture _postgres;
    private readonly RabbitMqFixture _rabbitMq;

    public RabbitMqOutboxRecoveryTests(
        PostgreSqlFixture postgres,
        RabbitMqFixture rabbitMq)
    {
        _postgres = postgres;
        _rabbitMq = rabbitMq;
    }

    [Fact]
    public async Task RabbitMq_RedrivesOutboxAfterOutage_AndInboxAbsorbsDuplicate()
    {
        await ClearMessagingStateAsync();
        var settings = BuildMessagingSettings();
        var recorder = new ProbeDeliveryRecorder();
        using var consumerHost = BuildHost(settings, recorder, addConsumer: true);
        using var producerHost = BuildHost(settings, recorder, addConsumer: false);

        await StartHostsAsync(consumerHost, producerHost, settings);
        try
        {
            var firstEventId = Guid.NewGuid();
            await StageAsync(producerHost.Services, firstEventId, sourceId: 101);
            await WaitUntilAsync(
                () => recorder.Count(firstEventId) == 1,
                TimeSpan.FromSeconds(60));

            await StageAsync(producerHost.Services, firstEventId, sourceId: 101);
            await WaitUntilOutboxIsEmptyAsync(firstEventId, TimeSpan.FromSeconds(60));
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

            var stopResult = await _rabbitMq.Container.ExecAsync(
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

            var startResult = await _rabbitMq.Container.ExecAsync(
                ["rabbitmqctl", "start_app"],
                TestContext.Current.CancellationToken);
            Assert.Equal(0, startResult.ExitCode);
            await WaitUntilAsync(
                () => recorder.Count(outageEventId) == 1,
                TimeSpan.FromSeconds(60));
            await WaitUntilOutboxIsEmptyAsync(
                outageEventId,
                TimeSpan.FromSeconds(60));
        }
        finally
        {
            await _rabbitMq.Container.ExecAsync(
                ["rabbitmqctl", "start_app"],
                CancellationToken.None);
            await producerHost.StopAsync(CancellationToken.None);
            await consumerHost.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RabbitMq_TwoWorkerHosts_CompeteForOneUserResult()
    {
        await ClearMessagingStateAsync();
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_postgres);
        var settings = BuildMessagingSettings();
        var recorder = new ProbeDeliveryRecorder();
        using var firstConsumer = BuildHost(settings, recorder, addConsumer: true);
        using var secondConsumer = BuildHost(settings, recorder, addConsumer: true);
        using var producerHost = BuildHost(settings, recorder, addConsumer: false);

        await firstConsumer.StartAsync(TestContext.Current.CancellationToken);
        await secondConsumer.StartAsync(TestContext.Current.CancellationToken);
        await producerHost.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitForQueueBindingsAsync(settings);
            await WaitForQueueConsumerCountAsync(settings, expectedCount: 2);
            var eventId = Guid.NewGuid();
            await StageAsync(
                producerHost.Services,
                eventId,
                assignment.AssignmentId,
                assignment.ReviewerId);
            await WaitForProbeAsync(recorder, eventId);
            await WaitForNotificationAsync(eventId, assignment.ReviewerId);
            await WaitUntilOutboxIsEmptyAsync(eventId, TimeSpan.FromSeconds(60));
            await Task.Delay(
                TimeSpan.FromMilliseconds(500),
                TestContext.Current.CancellationToken);

            await using var verificationDb = _postgres.NewDbContext();
            Assert.Equal(1, recorder.Count(eventId));
            Assert.Equal(1, await verificationDb.Notifications.CountAsync(
                item => item.EventId == eventId
                    && item.UserId == assignment.ReviewerId,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            await producerHost.StopAsync(CancellationToken.None);
            await secondConsumer.StopAsync(CancellationToken.None);
            await firstConsumer.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RabbitMq_QueuedMessage_IsConsumedAfterWorkerRestart()
    {
        await ClearMessagingStateAsync();
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_postgres);
        var settings = BuildMessagingSettings();
        var recorder = new ProbeDeliveryRecorder();
        using var firstConsumer = BuildHost(settings, recorder, addConsumer: true);
        using var producerHost = BuildHost(settings, recorder, addConsumer: false);

        await StartHostsAsync(firstConsumer, producerHost, settings);
        await firstConsumer.StopAsync(CancellationToken.None);
        await WaitForQueueConsumerCountAsync(settings, expectedCount: 0);
        var eventId = Guid.NewGuid();
        await StageAsync(
            producerHost.Services,
            eventId,
            assignment.AssignmentId,
            assignment.ReviewerId);
        await WaitUntilOutboxIsEmptyAsync(eventId, TimeSpan.FromSeconds(60));
        Assert.Equal(0, recorder.Count(eventId));

        using var restartedConsumer = BuildHost(settings, recorder, addConsumer: true);
        await restartedConsumer.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitForQueueBindingsAsync(settings);
            await WaitForQueueConsumerCountAsync(settings, expectedCount: 1);
            await WaitForProbeAsync(recorder, eventId);
            await WaitForNotificationAsync(eventId, assignment.ReviewerId);
            await Task.Delay(
                TimeSpan.FromMilliseconds(500),
                TestContext.Current.CancellationToken);

            await using var verificationDb = _postgres.NewDbContext();
            Assert.Equal(1, recorder.Count(eventId));
            Assert.Equal(1, await verificationDb.Notifications.CountAsync(
                item => item.EventId == eventId
                    && item.UserId == assignment.ReviewerId,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            await restartedConsumer.StopAsync(CancellationToken.None);
            await producerHost.StopAsync(CancellationToken.None);
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

        await StartHostsAsync(consumerHost, producerHost, settings);
        try
        {
            var eventId = Guid.NewGuid();
            await StageAsync(
                producerHost.Services,
                eventId,
                assignment.AssignmentId,
                assignment.ReviewerId);
            await WaitForProbeAsync(recorder, eventId);
            await WaitUntilAsync(async () =>
            {
                await using var db = _postgres.NewDbContext();
                return await db.Notifications.AnyAsync(
                    item => item.EventId == eventId
                        && item.UserId == assignment.ReviewerId,
                    TestContext.Current.CancellationToken);
            }, TimeSpan.FromSeconds(60));

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
        await StartHostsAsync(consumerHost, producerHost, settings);
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
            await WaitForProbeAsync(recorder, assignedEventId);
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

    [Fact]
    public async Task RabbitMq_KeyDropFanouts_RespectRelationshipsAndPreferences()
    {
        await ClearMessagingStateAsync();
        await using (var cleanupDb = _postgres.NewDbContext())
        {
            var now = DateTimeOffset.UtcNow;
            await cleanupDb.KeyDrops
                .Where(item => item.OpensAt <= now
                    && item.ClosedAt == null
                    && item.OpenNotificationStagedAt == null)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        item => item.OpenNotificationStagedAt,
                        now),
                    TestContext.Current.CancellationToken);
        }

        long ownerId;
        long recipientId;
        long cancelledRecipientId;
        long gameId;
        await using (var arrangeDb = _postgres.NewDbContext())
        {
            ownerId = (await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0)).Id;
            recipientId = (await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0)).Id;
            cancelledRecipientId = (await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0)).Id;
            var game = await ReviewTestData.CreateGameAsync(arrangeDb, ownerId);
            gameId = game.Id;
            arrangeDb.FavoriteGames.AddRange(
                Favorite(ownerId, gameId),
                Favorite(recipientId, gameId));
            arrangeDb.NotificationPreferences.Add(new NotificationPreference
            {
                UserId = recipientId,
                Kind = NotificationKind.FavoriteGameKeyDropCreated,
                WebEnabled = false,
                EmailEnabled = false,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var settings = BuildMessagingSettings();
        var recorder = new ProbeDeliveryRecorder();
        using var consumerHost = BuildHost(settings, recorder, addConsumer: true);
        using var producerHost = BuildHost(settings, recorder, addConsumer: false);
        await StartHostsAsync(consumerHost, producerHost, settings);
        try
        {
            ScheduleKeyDropResult schedule;
            await using (var scope = producerHost.Services.CreateAsyncScope())
            {
                schedule = await scope.ServiceProvider
                    .GetRequiredService<KeyDropSchedulingService>()
                    .ScheduleAsync(
                        gameId,
                        ownerId,
                        DateTimeOffset.UtcNow.AddHours(1),
                        $"KEY-{Guid.NewGuid():N}");
            }

            Assert.Equal(ScheduleKeyDropOutcome.Scheduled, schedule.Outcome);
            var dropId = schedule.KeyDropId!.Value;
            var favoriteEventId = NotificationEventId.Create(
                NotificationKind.FavoriteGameKeyDropCreated,
                dropId,
                recipientId);
            await WaitForNotificationAsync(favoriteEventId, recipientId);
            var favoriteFanoutId = NotificationEventId.Create(
                NotificationKind.FavoriteGameKeyDropCreated,
                dropId,
                ownerId);
            await using (var scope = producerHost.Services.CreateAsyncScope())
            {
                var publisher = scope.ServiceProvider
                    .GetRequiredService<IKeyDropNotificationFanoutPublisher>();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await publisher.StageFavoriteGameCreatedAsync(
                    favoriteFanoutId,
                    dropId,
                    DateTimeOffset.UtcNow,
                    TestContext.Current.CancellationToken);
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }
            await WaitUntilOutboxIsEmptyAsync(
                favoriteFanoutId,
                TimeSpan.FromSeconds(60));

            DateTimeOffset opensAt;
            await using (var arrangeDb = _postgres.NewDbContext())
            {
                var drop = await arrangeDb.KeyDrops.SingleAsync(
                    item => item.Id == dropId,
                    TestContext.Current.CancellationToken);
                opensAt = DateTimeOffset.UtcNow.AddMinutes(-1);
                drop.OpensAt = opensAt;
                arrangeDb.KeyDropSubscriptions.AddRange(
                    Subscription(ownerId, dropId),
                    Subscription(recipientId, dropId),
                    Subscription(cancelledRecipientId, dropId));
                await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
                await arrangeDb.KeyDropSubscriptions
                    .Where(item => item.UserId == cancelledRecipientId
                        && item.DropId == dropId)
                    .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
            }

            await using (var scope = producerHost.Services.CreateAsyncScope())
            {
                var opens = scope.ServiceProvider
                    .GetRequiredService<KeyDropOpenNotificationService>();
                Assert.Equal(
                    1,
                    (await opens.StageDueAsync(
                        cancellationToken: TestContext.Current.CancellationToken))
                    .StagedCount);
                Assert.Equal(
                    0,
                    (await opens.StageDueAsync(
                        cancellationToken: TestContext.Current.CancellationToken))
                    .StagedCount);
            }

            var openedEventId = NotificationEventId.Create(
                NotificationKind.KeyDropOpened,
                dropId,
                recipientId);
            await WaitForNotificationAsync(openedEventId, recipientId);

            await using var verificationDb = _postgres.NewDbContext();
            var notifications = await verificationDb.Notifications
                .Include(item => item.Deliveries)
                .Where(item => item.SourceType == NotificationSourceTypes.KeyDrop
                    && item.SourceId == dropId)
                .ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, notifications.Count);
            Assert.DoesNotContain(notifications, item => item.UserId == ownerId);
            Assert.DoesNotContain(
                notifications,
                item => item.UserId == cancelledRecipientId);

            var favorite = Assert.Single(
                notifications,
                item => item.Kind == NotificationKind.FavoriteGameKeyDropCreated);
            Assert.False(favorite.IsWebVisible);
            Assert.All(
                favorite.Deliveries,
                delivery => Assert.Equal(
                    NotificationDeliveryStatus.Suppressed,
                    delivery.Status));

            var opened = Assert.Single(
                notifications,
                item => item.Kind == NotificationKind.KeyDropOpened);
            Assert.True(opened.IsWebVisible);
            Assert.Contains(
                opened.Deliveries,
                delivery => delivery.Channel == NotificationChannel.Web
                    && delivery.Status == NotificationDeliveryStatus.Delivered);
            Assert.Contains(
                opened.Deliveries,
                delivery => delivery.Channel == NotificationChannel.Email
                    && delivery.Status == NotificationDeliveryStatus.Pending);
        }
        finally
        {
            await producerHost.StopAsync(CancellationToken.None);
            await consumerHost.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RabbitMq_GamePublishedFanout_RespectsCurrentFollowersAndPreferences()
    {
        await ClearMessagingStateAsync();
        long ownerId;
        long followerId;
        long removedFollowerId;
        await using (var arrangeDb = _postgres.NewDbContext())
        {
            ownerId = (await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0)).Id;
            followerId = (await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0)).Id;
            removedFollowerId = (await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0)).Id;
            arrangeDb.UserFollows.AddRange(
                Follow(followerId, ownerId),
                Follow(removedFollowerId, ownerId));
            arrangeDb.NotificationPreferences.Add(new NotificationPreference
            {
                UserId = followerId,
                Kind = NotificationKind.FollowedUserGamePublished,
                WebEnabled = false,
                EmailEnabled = false,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            await arrangeDb.UserFollows
                .Where(item => item.FollowerId == removedFollowerId
                    && item.FollowedUserId == ownerId)
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        var settings = BuildMessagingSettings();
        var recorder = new ProbeDeliveryRecorder();
        using var consumerHost = BuildHost(settings, recorder, addConsumer: true);
        using var producerHost = BuildHost(settings, recorder, addConsumer: false);
        await StartHostsAsync(consumerHost, producerHost, settings);
        try
        {
            CreateGameResult result;
            await using (var scope = producerHost.Services.CreateAsyncScope())
            {
                result = await scope.ServiceProvider
                    .GetRequiredService<GameManagementService>()
                    .CreateAsync(
                        ownerId,
                        $"Rabbit fan-out {Guid.NewGuid():N}",
                        "Actual social notification path.",
                        new GameClassificationInput("action", [], ["windows"]),
                        [],
                        TestContext.Current.CancellationToken);
            }

            Assert.Equal(CreateGameOutcome.Created, result.Outcome);
            var gameId = result.GameId!.Value;
            var recipientEventId = NotificationEventId.Create(
                NotificationKind.FollowedUserGamePublished,
                gameId,
                followerId);
            await WaitForNotificationAsync(recipientEventId, followerId);

            var fanoutId = NotificationEventId.Create(
                NotificationKind.FollowedUserGamePublished,
                gameId,
                ownerId);
            await using (var scope = producerHost.Services.CreateAsyncScope())
            {
                var publisher = scope.ServiceProvider
                    .GetRequiredService<ISocialNotificationFanoutPublisher>();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await publisher.StageGamePublishedAsync(
                    fanoutId,
                    gameId,
                    DateTimeOffset.UtcNow,
                    TestContext.Current.CancellationToken);
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }
            await WaitUntilOutboxIsEmptyAsync(fanoutId, TimeSpan.FromSeconds(60));

            await using var verificationDb = _postgres.NewDbContext();
            var notifications = await verificationDb.Notifications
                .Include(item => item.Deliveries)
                .Where(item => item.SourceType == NotificationSourceTypes.Game
                    && item.SourceId == gameId
                    && item.Kind == NotificationKind.FollowedUserGamePublished)
                .ToListAsync(TestContext.Current.CancellationToken);
            var notification = Assert.Single(notifications);
            Assert.Equal(followerId, notification.UserId);
            Assert.NotEqual(ownerId, notification.UserId);
            Assert.NotEqual(removedFollowerId, notification.UserId);
            Assert.False(notification.IsWebVisible);
            Assert.All(
                notification.Deliveries,
                delivery => Assert.Equal(
                    NotificationDeliveryStatus.Suppressed,
                    delivery.Status));
        }
        finally
        {
            await producerHost.StopAsync(CancellationToken.None);
            await consumerHost.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RabbitMq_PermanentConsumerFailure_RetriesThenMovesToErrorQueue()
    {
        var settings = BuildMessagingSettings();
        var recorder = new ProbeDeliveryRecorder();
        using var consumerHost = BuildHost(
            settings,
            recorder,
            addConsumer: false,
            addFailureProbeConsumer: true);
        await consumerHost.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var prefix = settings["Messaging:RabbitMq:EndpointPrefix"]!;
            var queueName = $"{prefix}-rabbit-mq-failure-probe";
            var errorQueueName = $"{queueName}_error";
            await WaitUntilAsync(
                async () => await GetQueueValueAsync(queueName, "consumers") == 1,
                TimeSpan.FromSeconds(30));

            var eventId = Guid.NewGuid();
            var bus = consumerHost.Services.GetRequiredService<IBus>();
            await bus.Publish(
                new RabbitMqFailureProbe(eventId),
                TestContext.Current.CancellationToken);

            await WaitUntilAsync(
                async () => await GetQueueValueAsync(
                    errorQueueName,
                    "messages_ready") == 1,
                TimeSpan.FromSeconds(75));

            Assert.Equal(4, recorder.Count(eventId));
            Assert.Equal(0, await GetQueueValueAsync(queueName, "messages_ready"));
        }
        finally
        {
            await consumerHost.StopAsync(CancellationToken.None);
        }
    }

    private IHost BuildHost(
        IReadOnlyDictionary<string, string?> settings,
        ProbeDeliveryRecorder recorder,
        bool addConsumer,
        bool addFailureProbeConsumer = false)
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

                if (addFailureProbeConsumer)
                {
                    registration.AddConsumer<RabbitMqFailureProbeConsumer>();
                }
            });
        builder.Services.AddScoped<ReviewClaimService>();
        builder.Services.AddScoped<FeedbackSubmissionService>();
        builder.Services.AddScoped<ReviewDeadlineNotificationService>();
        builder.Services.AddScoped<ReviewLeaseSweepService>();
        builder.Services.AddSingleton(
            TestAccountProtection.CreateGameKeyProtectionService());
        builder.Services.AddScoped<GameKeyRegistrationWriter>();
        builder.Services.AddScoped<KeyDropSchedulingService>();
        builder.Services.AddScoped<KeyDropOpenNotificationService>();
        builder.Services.AddScoped<GameManagementService>();
        return builder.Build();
    }

    private IReadOnlyDictionary<string, string?> BuildMessagingSettings()
    {
        var connection = new Uri(_rabbitMq.Container.GetConnectionString());
        return new Dictionary<string, string?>
        {
            ["Messaging:RabbitMq:Enabled"] = "true",
            ["Messaging:RabbitMq:Host"] = connection.Host,
            ["Messaging:RabbitMq:Port"] = connection.Port.ToString(),
            ["Messaging:RabbitMq:VirtualHost"] = "/",
            ["Messaging:RabbitMq:Username"] = RabbitMqFixture.Username,
            ["Messaging:RabbitMq:Password"] = RabbitMqFixture.Password,
            ["Messaging:RabbitMq:EndpointPrefix"] =
                $"indiefi-test-{Guid.NewGuid():N}"[..45],
        };
    }

    private async Task StartHostsAsync(
        IHost consumerHost,
        IHost producerHost,
        IReadOnlyDictionary<string, string?> settings)
    {
        await consumerHost.StartAsync(TestContext.Current.CancellationToken);
        await producerHost.StartAsync(TestContext.Current.CancellationToken);

        await WaitForQueueBindingsAsync(settings);
    }

    private async Task WaitForQueueBindingsAsync(
        IReadOnlyDictionary<string, string?> settings)
    {

        var prefix = settings["Messaging:RabbitMq:EndpointPrefix"]!;
        var expectedQueues = new HashSet<string>(StringComparer.Ordinal)
        {
            $"{prefix}-rabbit-mq-probe",
            $"{prefix}-user-notification-requested",
        };
        await WaitUntilAsync(async () =>
        {
            var bindings = await _rabbitMq.Container.ExecAsync(
                ["rabbitmqctl", "list_bindings", "source_name", "destination_name"],
                TestContext.Current.CancellationToken);
            if (bindings.ExitCode != 0)
            {
                return false;
            }

            var boundQueues = bindings.Stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r').Split('\t'))
                .Where(columns => columns.Length >= 2
                    && !string.IsNullOrWhiteSpace(columns[0]))
                .Select(columns => columns[1])
                .ToHashSet(StringComparer.Ordinal);
            return expectedQueues.IsSubsetOf(boundQueues);
        }, TimeSpan.FromSeconds(30));
    }

    private async Task WaitForQueueConsumerCountAsync(
        IReadOnlyDictionary<string, string?> settings,
        int expectedCount)
    {
        var prefix = settings["Messaging:RabbitMq:EndpointPrefix"]!;
        var expectedQueues = new HashSet<string>(StringComparer.Ordinal)
        {
            $"{prefix}-rabbit-mq-probe",
            $"{prefix}-user-notification-requested",
        };
        await WaitUntilAsync(async () =>
        {
            var queues = await _rabbitMq.Container.ExecAsync(
                ["rabbitmqctl", "list_queues", "name", "consumers"],
                TestContext.Current.CancellationToken);
            if (queues.ExitCode != 0)
            {
                return false;
            }

            var matching = queues.Stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r').Split('\t'))
                .Where(columns => columns.Length >= 2
                    && expectedQueues.Contains(columns[0])
                    && int.TryParse(columns[1], out _))
                .ToDictionary(
                    columns => columns[0],
                    columns => int.Parse(columns[1]),
                    StringComparer.Ordinal);
            return expectedQueues.All(queue =>
                matching.GetValueOrDefault(queue, -1) == expectedCount);
        }, TimeSpan.FromSeconds(30));
    }

    private async Task<int> GetQueueValueAsync(string queueName, string column)
    {
        var queues = await _rabbitMq.Container.ExecAsync(
            ["rabbitmqctl", "list_queues", "name", column],
            TestContext.Current.CancellationToken);
        if (queues.ExitCode != 0)
        {
            return -1;
        }

        var row = queues.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\t'))
            .SingleOrDefault(columns =>
                columns.Length >= 2
                && string.Equals(columns[0], queueName, StringComparison.Ordinal));
        return row is not null && int.TryParse(row[1], out var value)
            ? value
            : -1;
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
        }, TimeSpan.FromSeconds(60));

    private async Task WaitForProbeAsync(
        ProbeDeliveryRecorder recorder,
        Guid eventId)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(60);
        while (DateTimeOffset.UtcNow < expiresAt)
        {
            if (recorder.Count(eventId) == 1)
            {
                return;
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(100),
                TestContext.Current.CancellationToken);
        }

        await using var db = _postgres.NewDbContext();
        var matchingOutbox = await db.Set<OutboxMessage>().CountAsync(
            item => item.MessageId == eventId,
            TestContext.Current.CancellationToken);
        var allOutbox = await db.Set<OutboxMessage>().CountAsync(
            TestContext.Current.CancellationToken);
        var matchingInbox = await db.Set<InboxState>().CountAsync(
            item => item.MessageId == eventId,
            TestContext.Current.CancellationToken);
        var brokerQueues = await _rabbitMq.Container.ExecAsync(
            [
                "rabbitmqctl",
                "list_queues",
                "name",
                "messages_ready",
                "messages_unacknowledged",
            ],
            TestContext.Current.CancellationToken);
        Assert.Fail(
            $"Probe message was not consumed. MatchingOutbox={matchingOutbox}, "
            + $"AllOutbox={allOutbox}, MatchingInbox={matchingInbox}, "
            + $"RabbitQueues={brokerQueues.Stdout}");
    }

    private static FavoriteGame Favorite(long userId, long gameId) => new()
    {
        UserId = userId,
        GameId = gameId,
    };

    private static KeyDropSubscription Subscription(long userId, long dropId) => new()
    {
        UserId = userId,
        DropId = dropId,
    };

    private static UserFollow Follow(long followerId, long followedUserId) => new()
    {
        FollowerId = followerId,
        FollowedUserId = followedUserId,
    };

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

public sealed record RabbitMqFailureProbe(Guid EventId);

public sealed class RabbitMqFailureProbeConsumer :
    IConsumer<RabbitMqFailureProbe>
{
    private readonly ProbeDeliveryRecorder _recorder;

    public RabbitMqFailureProbeConsumer(ProbeDeliveryRecorder recorder)
    {
        _recorder = recorder;
    }

    public Task Consume(ConsumeContext<RabbitMqFailureProbe> context)
    {
        _recorder.Record(context.Message.EventId);
        throw new InvalidOperationException("Expected isolated consumer failure.");
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
