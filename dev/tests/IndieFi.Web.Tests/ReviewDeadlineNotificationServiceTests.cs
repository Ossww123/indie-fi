using System.Diagnostics;
using IndieFi.Web.Data;
using IndieFi.Web.Modules.Feedback.Application;
using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Contracts;
using IndieFi.Web.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class ReviewDeadlineNotificationServiceTests
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(10);

    private readonly PostgreSqlFixture _fixture;

    public ReviewDeadlineNotificationServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task StageDue_ClaimsOnceAndStagesStableDeadlineEvent()
    {
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);
        var leasedUntil = await SetLeaseAsync(
            assignment.AssignmentId,
            TimeSpan.FromMinutes(5));
        var publisher = new TestNotificationEventPublisher();
        await using var db = _fixture.NewDbContext();
        var service = new ReviewDeadlineNotificationService(db, publisher);

        var first = await service.StageDueAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        var replay = await service.StageDueAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, first.StagedCount);
        Assert.Equal(0, replay.StagedCount);
        var staged = Assert.Single(publisher.Events);
        Assert.Equal(
            NotificationEventId.Create(
                NotificationKind.ReviewDeadlineSoon,
                assignment.AssignmentId,
                assignment.ReviewerId),
            staged.EventId);
        Assert.Equal(assignment.ReviewerId, staged.RecipientUserId);
        Assert.Equal(NotificationKind.ReviewDeadlineSoon, staged.Kind);
        Assert.Equal(NotificationSourceTypes.ReviewAssignment, staged.SourceType);
        Assert.Equal(assignment.AssignmentId, staged.SourceId);
        Assert.Equal(
            leasedUntil - NotificationPolicy.ReviewDeadlineLeadTime,
            staged.OccurredAt);
        Assert.NotNull(await db.ReviewAssignments
            .Where(item => item.Id == assignment.AssignmentId)
            .Select(item => item.DeadlineNotificationStagedAt)
            .SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StageDue_IgnoresAssignmentsOutsideActiveWindow()
    {
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);
        await SetLeaseAsync(
            assignment.AssignmentId,
            TimeSpan.FromMinutes(11));
        var publisher = new TestNotificationEventPublisher();

        await using (var futureDb = _fixture.NewDbContext())
        {
            var result = await new ReviewDeadlineNotificationService(
                futureDb,
                publisher).StageDueAsync(
                    cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(0, result.StagedCount);
        }

        await SetLeaseAsync(
            assignment.AssignmentId,
            TimeSpan.FromMinutes(-1));
        await using var expiredDb = _fixture.NewDbContext();
        var expiredResult = await new ReviewDeadlineNotificationService(
            expiredDb,
            publisher).StageDueAsync(
                cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, expiredResult.StagedCount);
        Assert.Empty(publisher.Events);
    }

    [Fact]
    public async Task StageDue_WhenPublishingFails_RollsBackClaimForRetry()
    {
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);
        await SetLeaseAsync(
            assignment.AssignmentId,
            TimeSpan.FromMinutes(5));

        await using (var db = _fixture.NewDbContext())
        {
            var service = new ReviewDeadlineNotificationService(
                db,
                new TestNotificationEventPublisher
                {
                    Failure = new InvalidOperationException("staging failed"),
                });
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.StageDueAsync(
                cancellationToken: TestContext.Current.CancellationToken));
        }

        await using var verificationDb = _fixture.NewDbContext();
        Assert.Null(await verificationDb.ReviewAssignments
            .Where(item => item.Id == assignment.AssignmentId)
            .Select(item => item.DeadlineNotificationStagedAt)
            .SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StageDue_ConcurrentInstances_ClaimOneEvent()
    {
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);
        await SetLeaseAsync(
            assignment.AssignmentId,
            TimeSpan.FromMinutes(5));
        var firstPublisher = new TestNotificationEventPublisher();
        var secondPublisher = new TestNotificationEventPublisher();
        await using var firstDb = _fixture.NewDbContext();
        await using var secondDb = _fixture.NewDbContext();

        var results = await Task.WhenAll(
            new ReviewDeadlineNotificationService(firstDb, firstPublisher)
                .StageDueAsync(cancellationToken: TestContext.Current.CancellationToken),
            new ReviewDeadlineNotificationService(secondDb, secondPublisher)
                .StageDueAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(1, results.Sum(item => item.StagedCount));
        Assert.Equal(1, firstPublisher.Events.Count + secondPublisher.Events.Count);
    }

    [Fact]
    public async Task Worker_StagesDueAssignmentWithoutHttpRequest()
    {
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);
        await SetLeaseAsync(
            assignment.AssignmentId,
            TimeSpan.FromMinutes(5));
        var publisher = new TestNotificationEventPublisher();
        using var provider = BuildServiceProvider(publisher);
        var worker = new ReviewDeadlineNotificationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<ILogger<ReviewDeadlineNotificationWorker>>(),
            TimeSpan.FromMilliseconds(50));

        try
        {
            await worker.StartAsync(TestContext.Current.CancellationToken);
            await WaitUntilStagedAsync(assignment.AssignmentId);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        var staged = Assert.Single(publisher.Events);
        Assert.Equal(assignment.AssignmentId, staged.SourceId);
    }

    private ServiceProvider BuildServiceProvider(
        TestNotificationEventPublisher publisher)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(_fixture.ConnectionString));
        services.AddSingleton<INotificationEventPublisher>(publisher);
        services.AddScoped<ReviewDeadlineNotificationService>();
        services.AddLogging();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private async Task<DateTimeOffset> SetLeaseAsync(
        long assignmentId,
        TimeSpan offset)
    {
        await using var db = _fixture.NewDbContext();
        var leasedUntil = DateTimeOffset.UtcNow.Add(offset);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE review_assignments
               SET leased_until = {leasedUntil},
                   deadline_notification_staged_at = NULL
             WHERE id = {assignmentId}
            """,
            TestContext.Current.CancellationToken);
        return await db.ReviewAssignments
            .Where(item => item.Id == assignmentId)
            .Select(item => item.LeasedUntil)
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    private async Task WaitUntilStagedAsync(long assignmentId)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < PollTimeout)
        {
            await using var db = _fixture.NewDbContext();
            if (await db.ReviewAssignments.AnyAsync(
                    item => item.Id == assignmentId
                        && item.DeadlineNotificationStagedAt != null,
                    TestContext.Current.CancellationToken))
            {
                return;
            }

            await Task.Delay(PollInterval, TestContext.Current.CancellationToken);
        }

        Assert.Fail("The worker did not stage the deadline event within ten seconds.");
    }
}
