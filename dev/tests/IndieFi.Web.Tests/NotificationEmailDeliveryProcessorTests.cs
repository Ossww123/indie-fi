using System.Net.Mail;
using IndieFi.Web.Data;
using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Contracts;
using IndieFi.Web.Modules.Notifications.Domain;
using IndieFi.Web.Modules.Notifications.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class NotificationEmailDeliveryProcessorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 16, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlFixture _fixture;

    public NotificationEmailDeliveryProcessorTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ProcessBatch_SuccessMarksDeliveryDelivered()
    {
        await ResetNotificationsAsync();
        var deliveryId = await ArrangeDeliveryAsync("sent@example.test");
        var sender = new RecordingSender();

        await using (var db = _fixture.NewDbContext())
        {
            var result = await NewProcessor(db, sender, Now).ProcessBatchAsync(
                10,
                TestContext.Current.CancellationToken);
            Assert.Equal(1, result.SentCount);
        }

        var request = Assert.Single(sender.Requests);
        await using var verificationDb = _fixture.NewDbContext();
        var delivery = await verificationDb.NotificationDeliveries.SingleAsync(
            item => item.Id == deliveryId,
            TestContext.Current.CancellationToken);
        Assert.Equal(delivery.MessageId, request.MessageId);
        Assert.Equal(NotificationDeliveryStatus.Delivered, delivery.Status);
        Assert.Equal(1, delivery.AttemptCount);
        Assert.Equal(Now, delivery.LastAttemptAt);
        Assert.Equal(Now, delivery.DeliveredAt);
        Assert.Null(delivery.NextAttemptAt);
        Assert.Null(delivery.LastErrorCode);
    }

    [Fact]
    public async Task ProcessBatch_ExpiredSourceDoesNotCallSmtp()
    {
        await ResetNotificationsAsync();
        var deliveryId = await ArrangeDeliveryAsync(
            "expired@example.test",
            expiresAt: Now.AddMinutes(-1),
            createdAt: Now.AddHours(-1));
        var sender = new RecordingSender();

        await using (var db = _fixture.NewDbContext())
        {
            var result = await NewProcessor(db, sender, Now).ProcessBatchAsync(
                10,
                TestContext.Current.CancellationToken);
            Assert.Equal(1, result.ExpiredCount);
        }

        Assert.Empty(sender.Requests);
        await using var verificationDb = _fixture.NewDbContext();
        var delivery = await verificationDb.NotificationDeliveries.SingleAsync(
            item => item.Id == deliveryId,
            TestContext.Current.CancellationToken);
        Assert.Equal(NotificationDeliveryStatus.Expired, delivery.Status);
        Assert.Equal(0, delivery.AttemptCount);
        Assert.Equal(Now, delivery.FailedAt);
        Assert.Equal("stale_or_expired_source", delivery.LastErrorCode);
    }

    [Fact]
    public async Task ProcessBatch_TransientFailuresUseFiveDelaysThenFailOnSixthAttempt()
    {
        await ResetNotificationsAsync();
        var deliveryId = await ArrangeDeliveryAsync("retry@example.test");
        var sender = new AlwaysFailsSender(new SmtpException(SmtpStatusCode.ServiceNotAvailable));
        var attemptAt = Now;

        for (var attempt = 1; attempt <= NotificationPolicy.EmailMaxAttempts; attempt++)
        {
            await using var db = _fixture.NewDbContext();
            var result = await NewProcessor(db, sender, attemptAt).ProcessBatchAsync(
                1,
                TestContext.Current.CancellationToken);
            if (attempt < NotificationPolicy.EmailMaxAttempts)
            {
                Assert.Equal(1, result.RescheduledCount);
                await using var verificationDb = _fixture.NewDbContext();
                var pending = await verificationDb.NotificationDeliveries.SingleAsync(
                    item => item.Id == deliveryId,
                    TestContext.Current.CancellationToken);
                Assert.Equal(NotificationDeliveryStatus.Pending, pending.Status);
                Assert.Equal(attempt, pending.AttemptCount);
                Assert.Equal(
                    attemptAt.Add(NotificationPolicy.EmailRetryDelays[attempt - 1]),
                    pending.NextAttemptAt);
                attemptAt = pending.NextAttemptAt!.Value;
            }
            else
            {
                Assert.Equal(1, result.FailedCount);
            }
        }

        await using var finalDb = _fixture.NewDbContext();
        var failed = await finalDb.NotificationDeliveries.SingleAsync(
            item => item.Id == deliveryId,
            TestContext.Current.CancellationToken);
        Assert.Equal(NotificationDeliveryStatus.Failed, failed.Status);
        Assert.Equal(NotificationPolicy.EmailMaxAttempts, failed.AttemptCount);
        Assert.Null(failed.NextAttemptAt);
        Assert.Equal(attemptAt, failed.FailedAt);
        Assert.Equal(
            "retry_exhausted_smtp_transient_failure",
            failed.LastErrorCode);
    }

    [Fact]
    public async Task ProcessBatch_Smtp5xxImmediatelyMovesToFailed()
    {
        await ResetNotificationsAsync();
        var deliveryId = await ArrangeDeliveryAsync("rejected@example.test");
        var sender = new AlwaysFailsSender(
            new SmtpException(SmtpStatusCode.MailboxUnavailable));

        await using (var db = _fixture.NewDbContext())
        {
            var result = await NewProcessor(db, sender, Now).ProcessBatchAsync(
                10,
                TestContext.Current.CancellationToken);
            Assert.Equal(1, result.FailedCount);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var failed = await verificationDb.NotificationDeliveries.SingleAsync(
            item => item.Id == deliveryId,
            TestContext.Current.CancellationToken);
        Assert.Equal(NotificationDeliveryStatus.Failed, failed.Status);
        Assert.Equal(1, failed.AttemptCount);
        Assert.Equal("smtp_permanent_rejection", failed.LastErrorCode);
    }

    [Fact]
    public async Task ProcessBatch_OneTransientFailureDoesNotBlockNextDelivery()
    {
        await ResetNotificationsAsync();
        const string blockedAddress = "blocked@example.test";
        await ArrangeDeliveryAsync(blockedAddress);
        await ArrangeDeliveryAsync("following@example.test");
        var sender = new FailsForRecipientSender(blockedAddress);

        await using var db = _fixture.NewDbContext();
        var result = await NewProcessor(db, sender, Now).ProcessBatchAsync(
            10,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.RescheduledCount);
        Assert.Equal(1, result.SentCount);
        Assert.Equal(
            "following@example.test",
            Assert.Single(sender.SentRequests).RecipientAddress);
    }

    private NotificationEmailDeliveryProcessor NewProcessor(
        AppDbContext db,
        INotificationEmailSender sender,
        DateTimeOffset now)
    {
        var options = Microsoft.Extensions.Options.Options.Create(
            new NotificationEmailDeliveryOptions
            {
                PublicBaseUrl = "https://indiefi.example/",
            });
        var authority = new NotificationEmailDeliveryAuthorityService(
            db,
            TestAccountProtection.CreateService(),
            new NotificationEmailTemplateRenderer(options));
        return new NotificationEmailDeliveryProcessor(
            db,
            authority,
            sender,
            new FixedTimeProvider(now));
    }

    private async Task<long> ArrangeDeliveryAsync(
        string email,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? createdAt = null)
    {
        var created = createdAt ?? Now;
        await using var db = _fixture.NewDbContext();
        var user = TestAccountProtection.AddUser(db, email);
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
            OccurredAt = created.AddMinutes(-1),
            CreatedAt = created,
            ExpiresAt = expiresAt ?? created.AddDays(90),
        };
        var delivery = new NotificationDelivery
        {
            MessageId = Guid.NewGuid(),
            Channel = NotificationChannel.Email,
            Status = NotificationDeliveryStatus.Pending,
            CreatedAt = created,
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

    private sealed class AlwaysFailsSender : INotificationEmailSender
    {
        private readonly Exception _exception;

        public AlwaysFailsSender(Exception exception)
        {
            _exception = exception;
        }

        public Task SendAsync(
            NotificationEmailSendRequest request,
            CancellationToken cancellationToken) => Task.FromException(_exception);
    }

    private sealed class FailsForRecipientSender : INotificationEmailSender
    {
        private readonly string _blockedAddress;

        public FailsForRecipientSender(string blockedAddress)
        {
            _blockedAddress = blockedAddress;
        }

        public List<NotificationEmailSendRequest> SentRequests { get; } = [];

        public Task SendAsync(
            NotificationEmailSendRequest request,
            CancellationToken cancellationToken)
        {
            if (request.RecipientAddress == _blockedAddress)
            {
                return Task.FromException(
                    new SmtpException(SmtpStatusCode.ServiceNotAvailable));
            }

            SentRequests.Add(request);
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
