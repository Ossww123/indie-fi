using IndieFi.Web.Data;
using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Contracts;
using IndieFi.Web.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class NotificationEmailOperationsServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 16, 11, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlFixture _fixture;

    public NotificationEmailOperationsServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SummaryAndFailures_ExposeCountsWithoutNotificationContent()
    {
        await ResetNotificationsAsync();
        await ArrangeDeliveryAsync(
            NotificationDeliveryStatus.Pending,
            nextAttemptAt: Now.AddMinutes(-1));
        await ArrangeDeliveryAsync(
            NotificationDeliveryStatus.Pending,
            nextAttemptAt: Now.AddMinutes(5));
        var failed = await ArrangeDeliveryAsync(
            NotificationDeliveryStatus.Failed,
            attemptCount: 6,
            failedAt: Now.AddMinutes(-2),
            errorCode: "retry_exhausted_smtp_transient_failure");
        await ArrangeDeliveryAsync(
            NotificationDeliveryStatus.Expired,
            failedAt: Now.AddMinutes(-3),
            errorCode: "stale_or_expired_source");

        await using var db = _fixture.NewDbContext();
        var service = new NotificationEmailOperationsService(db);
        var summary = await service.GetSummaryAsync(
            Now,
            TestContext.Current.CancellationToken);
        var failures = await service.GetFailuresAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, summary.PendingCount);
        Assert.Equal(1, summary.DueCount);
        Assert.Equal(1, summary.FailedCount);
        Assert.Equal(1, summary.ExpiredCount);
        Assert.Equal(Now.AddMinutes(-1), summary.OldestDueAt);
        var view = Assert.Single(failures);
        Assert.Equal(failed.DeliveryId, view.DeliveryId);
        Assert.Equal(failed.MessageId, view.MessageId);
        Assert.Equal(NotificationKind.AccountStatusChanged, view.Kind);
        Assert.Equal(6, view.AttemptCount);
        Assert.Equal("retry_exhausted_smtp_transient_failure", view.ErrorCode);
        Assert.DoesNotContain(
            typeof(NotificationEmailFailureView).GetProperties(),
            property => property.Name.Contains("Address", StringComparison.Ordinal)
                || property.Name.Contains("Title", StringComparison.Ordinal)
                || property.Name.Contains("Body", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RetryFailed_PreservesIdentityAndAttemptsWhileMakingItDue()
    {
        await ResetNotificationsAsync();
        var failed = await ArrangeDeliveryAsync(
            NotificationDeliveryStatus.Failed,
            attemptCount: 6,
            failedAt: Now.AddMinutes(-1),
            errorCode: "smtp_permanent_rejection");

        await using (var db = _fixture.NewDbContext())
        {
            var outcome = await new NotificationEmailOperationsService(db)
                .RetryFailedAsync(
                    failed.DeliveryId,
                    failed.MessageId,
                    Now,
                    TestContext.Current.CancellationToken);
            Assert.Equal(NotificationEmailRetryOutcome.Retried, outcome);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var delivery = await verificationDb.NotificationDeliveries.SingleAsync(
            item => item.Id == failed.DeliveryId,
            TestContext.Current.CancellationToken);
        Assert.Equal(failed.MessageId, delivery.MessageId);
        Assert.Equal(NotificationDeliveryStatus.Pending, delivery.Status);
        Assert.Equal(6, delivery.AttemptCount);
        Assert.Equal(Now, delivery.NextAttemptAt);
        Assert.Null(delivery.FailedAt);
        Assert.Equal("smtp_permanent_rejection", delivery.LastErrorCode);
    }

    [Fact]
    public async Task RetryFailed_ConcurrentRequestsHaveOneWinner()
    {
        await ResetNotificationsAsync();
        var failed = await ArrangeDeliveryAsync(
            NotificationDeliveryStatus.Failed,
            failedAt: Now.AddMinutes(-1),
            errorCode: "smtp_permanent_rejection");
        await using var firstDb = _fixture.NewDbContext();
        await using var secondDb = _fixture.NewDbContext();

        var outcomes = await Task.WhenAll(
            new NotificationEmailOperationsService(firstDb).RetryFailedAsync(
                failed.DeliveryId,
                failed.MessageId,
                Now,
                TestContext.Current.CancellationToken),
            new NotificationEmailOperationsService(secondDb).RetryFailedAsync(
                failed.DeliveryId,
                failed.MessageId,
                Now,
                TestContext.Current.CancellationToken));

        Assert.Equal(1, outcomes.Count(
            outcome => outcome == NotificationEmailRetryOutcome.Retried));
        Assert.Equal(1, outcomes.Count(
            outcome => outcome == NotificationEmailRetryOutcome.NotFailed));
    }

    [Fact]
    public async Task RetryFailed_DoesNotRetryExpiredOrWrongMessage()
    {
        await ResetNotificationsAsync();
        var expired = await ArrangeDeliveryAsync(
            NotificationDeliveryStatus.Expired,
            failedAt: Now.AddMinutes(-1),
            errorCode: "stale_or_expired_source");
        await using var db = _fixture.NewDbContext();
        var service = new NotificationEmailOperationsService(db);

        Assert.Equal(
            NotificationEmailRetryOutcome.NotFailed,
            await service.RetryFailedAsync(
                expired.DeliveryId,
                expired.MessageId,
                Now,
                TestContext.Current.CancellationToken));
        Assert.Equal(
            NotificationEmailRetryOutcome.NotFound,
            await service.RetryFailedAsync(
                expired.DeliveryId,
                Guid.NewGuid(),
                Now,
                TestContext.Current.CancellationToken));
    }

    private async Task<ArrangedDelivery> ArrangeDeliveryAsync(
        NotificationDeliveryStatus status,
        int attemptCount = 0,
        DateTimeOffset? nextAttemptAt = null,
        DateTimeOffset? failedAt = null,
        string? errorCode = null)
    {
        await using var db = _fixture.NewDbContext();
        var user = TestAccountProtection.AddUser(
            db,
            $"{Guid.NewGuid():N}@example.test");
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var notification = new Notification
        {
            UserId = user.Id,
            EventId = Guid.NewGuid(),
            Kind = NotificationKind.AccountStatusChanged,
            SourceType = NotificationSourceTypes.User,
            SourceId = user.Id,
            Title = $"Secret title {Guid.NewGuid():N}",
            Body = $"Secret body {Guid.NewGuid():N}",
            DestinationPath = "/Account/Profile",
            IsWebVisible = true,
            OccurredAt = Now.AddHours(-1),
            CreatedAt = Now.AddHours(-1),
            ExpiresAt = Now.AddDays(89),
        };
        var delivery = new NotificationDelivery
        {
            MessageId = Guid.NewGuid(),
            Channel = NotificationChannel.Email,
            Status = status,
            AttemptCount = attemptCount,
            CreatedAt = Now.AddHours(-1),
            NextAttemptAt = nextAttemptAt,
            LastAttemptAt = attemptCount > 0 ? Now.AddMinutes(-2) : null,
            FailedAt = failedAt,
            LastErrorCode = errorCode,
        };
        notification.Deliveries.Add(delivery);
        db.Notifications.Add(notification);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return new(delivery.Id, delivery.MessageId);
    }

    private async Task ResetNotificationsAsync()
    {
        await using var db = _fixture.NewOwnerDbContext();
        await db.Notifications.ExecuteDeleteAsync(
            TestContext.Current.CancellationToken);
    }

    private sealed record ArrangedDelivery(long DeliveryId, Guid MessageId);
}
