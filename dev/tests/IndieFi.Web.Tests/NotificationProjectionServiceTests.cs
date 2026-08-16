using IndieFi.Web.Data;
using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Contracts;
using IndieFi.Web.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class NotificationProjectionServiceTests
{
    private readonly PostgreSqlFixture _fixture;

    public NotificationProjectionServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Project_ActiveReviewAssignment_CreatesContentAndDefaultDeliveries()
    {
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);
        var message = Message(
            assignment.AssignmentId,
            assignment.ReviewerId,
            NotificationKind.ReviewAssigned);

        await using (var db = _fixture.NewDbContext())
        {
            var result = await Service(db).ProjectAsync(
                message,
                TestContext.Current.CancellationToken);
            Assert.Equal(NotificationProjectionOutcome.Created, result.Outcome);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var notification = await verificationDb.Notifications
            .Include(item => item.Deliveries)
            .SingleAsync(
                item => item.UserId == assignment.ReviewerId
                    && item.EventId == message.EventId,
                TestContext.Current.CancellationToken);
        Assert.Equal(NotificationKind.ReviewAssigned, notification.Kind);
        Assert.Contains("리뷰", notification.Title);
        Assert.Equal(
            $"/Reviews/Submit?assignmentId={assignment.AssignmentId}",
            notification.DestinationPath);
        Assert.True(notification.IsWebVisible);
        Assert.Contains(notification.Deliveries,
            item => item.Channel == NotificationChannel.Email
                && item.Status == NotificationDeliveryStatus.Pending);
    }

    [Fact]
    public async Task Project_UserOverride_SnapshotsSuppressedChannels()
    {
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);
        await using (var setupDb = _fixture.NewDbContext())
        {
            setupDb.NotificationPreferences.Add(new NotificationPreference
            {
                UserId = assignment.ReviewerId,
                Kind = NotificationKind.ReviewAssigned,
                WebEnabled = false,
                EmailEnabled = false,
            });
            await setupDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var message = Message(
            assignment.AssignmentId,
            assignment.ReviewerId,
            NotificationKind.ReviewAssigned);
        await using (var db = _fixture.NewDbContext())
        {
            Assert.Equal(
                NotificationProjectionOutcome.Created,
                (await Service(db).ProjectAsync(
                    message,
                    TestContext.Current.CancellationToken)).Outcome);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var notification = await verificationDb.Notifications
            .Include(item => item.Deliveries)
            .SingleAsync(
                item => item.EventId == message.EventId,
                TestContext.Current.CancellationToken);
        Assert.False(notification.IsWebVisible);
        Assert.All(notification.Deliveries,
            item => Assert.Equal(
                NotificationDeliveryStatus.Suppressed,
                item.Status));
    }

    [Fact]
    public async Task Project_SameRecipientEventTwice_ReturnsExistingNotification()
    {
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);
        var message = Message(
            assignment.AssignmentId,
            assignment.ReviewerId,
            NotificationKind.ReviewAssigned);

        await using var firstDb = _fixture.NewDbContext();
        var first = await Service(firstDb).ProjectAsync(
            message,
            TestContext.Current.CancellationToken);
        await using var secondDb = _fixture.NewDbContext();
        var second = await Service(secondDb).ProjectAsync(
            message,
            TestContext.Current.CancellationToken);

        Assert.Equal(NotificationProjectionOutcome.Created, first.Outcome);
        Assert.Equal(NotificationProjectionOutcome.AlreadyExists, second.Outcome);
        Assert.Equal(first.NotificationId, second.NotificationId);
    }

    [Fact]
    public async Task Project_SameRecipientEventConcurrently_CreatesOneNotification()
    {
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);
        var message = Message(
            assignment.AssignmentId,
            assignment.ReviewerId,
            NotificationKind.ReviewAssigned);
        await using var firstDb = _fixture.NewDbContext();
        await using var secondDb = _fixture.NewDbContext();

        var results = await Task.WhenAll(
            Service(firstDb).ProjectAsync(
                message,
                TestContext.Current.CancellationToken),
            Service(secondDb).ProjectAsync(
                message,
                TestContext.Current.CancellationToken));

        Assert.Contains(results,
            item => item.Outcome == NotificationProjectionOutcome.Created);
        Assert.Contains(results,
            item => item.Outcome == NotificationProjectionOutcome.AlreadyExists);
        Assert.Equal(results[0].NotificationId, results[1].NotificationId);
        await using var verificationDb = _fixture.NewDbContext();
        Assert.Equal(1, await verificationDb.Notifications.CountAsync(
            item => item.UserId == assignment.ReviewerId
                && item.EventId == message.EventId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Project_WrongRecipientOrSourceType_IsIgnored()
    {
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);
        var wrongRecipient = Message(
            assignment.AssignmentId,
            assignment.ReviewerId + 1,
            NotificationKind.ReviewAssigned);
        var wrongType = Message(
            assignment.AssignmentId,
            assignment.ReviewerId,
            NotificationKind.ReviewAssigned) with
        {
            SourceType = NotificationSourceTypes.Game,
        };

        await using var db = _fixture.NewDbContext();
        Assert.Equal(
            NotificationProjectionOutcome.IgnoredMissingOrStaleSource,
            (await Service(db).ProjectAsync(
                wrongRecipient,
                TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(
            NotificationProjectionOutcome.IgnoredInvalidContract,
            (await Service(db).ProjectAsync(
                wrongType,
                TestContext.Current.CancellationToken)).Outcome);
    }

    private static NotificationProjectionService Service(
        AppDbContext db) => new(
        db,
        new NotificationFactory(),
        TimeProvider.System);

    private static UserNotificationRequestedV1 Message(
        long sourceId,
        long recipientUserId,
        NotificationKind kind) => new(
        Guid.NewGuid(),
        recipientUserId,
        kind.ToString(),
        NotificationSourceTypes.For(kind),
        sourceId,
        DateTimeOffset.UtcNow);
}
