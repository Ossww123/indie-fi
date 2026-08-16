using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Contracts;
using IndieFi.Web.Modules.Notifications.Domain;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class NotificationFactoryTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 16, 3, 0, 0, TimeSpan.Zero);

    private readonly NotificationFactory _factory = new();

    [Fact]
    public void Create_WithDefaults_SnapshotsWebAndEmailDelivery()
    {
        var notification = _factory.Create(
            Message(NotificationKind.ReviewAssigned),
            NotificationKind.ReviewAssigned,
            Content(),
            preference: null,
            Now);

        Assert.True(notification.IsWebVisible);
        Assert.Equal(Now.AddDays(90), notification.ExpiresAt);
        var web = Assert.Single(notification.Deliveries,
            item => item.Channel == NotificationChannel.Web);
        Assert.Equal(NotificationDeliveryStatus.Delivered, web.Status);
        Assert.Equal(Now, web.DeliveredAt);
        var email = Assert.Single(notification.Deliveries,
            item => item.Channel == NotificationChannel.Email);
        Assert.Equal(NotificationDeliveryStatus.Pending, email.Status);
        Assert.Equal(Now, email.NextAttemptAt);
    }

    [Fact]
    public void Create_WithUserOverride_SuppressesBothChannels()
    {
        var notification = _factory.Create(
            Message(NotificationKind.ReviewAssigned),
            NotificationKind.ReviewAssigned,
            Content(),
            new NotificationPreference
            {
                Kind = NotificationKind.ReviewAssigned,
                WebEnabled = false,
                EmailEnabled = false,
            },
            Now);

        Assert.False(notification.IsWebVisible);
        Assert.All(notification.Deliveries, delivery =>
        {
            Assert.Equal(NotificationDeliveryStatus.Suppressed, delivery.Status);
            Assert.Null(delivery.NextAttemptAt);
            Assert.Null(delivery.DeliveredAt);
        });
    }

    [Fact]
    public void Create_MandatoryAccountEvent_IgnoresDisabledOverride()
    {
        var notification = _factory.Create(
            Message(NotificationKind.AccountStatusChanged),
            NotificationKind.AccountStatusChanged,
            Content(),
            new NotificationPreference
            {
                Kind = NotificationKind.AccountStatusChanged,
                WebEnabled = false,
                EmailEnabled = false,
            },
            Now);

        Assert.True(notification.IsWebVisible);
        Assert.Contains(notification.Deliveries,
            item => item.Channel == NotificationChannel.Email
                && item.Status == NotificationDeliveryStatus.Pending);
    }

    [Fact]
    public void Create_SameEvent_UsesStableDistinctChannelMessageIds()
    {
        var message = Message(NotificationKind.ReviewAssigned);
        var first = _factory.Create(
            message,
            NotificationKind.ReviewAssigned,
            Content(),
            preference: null,
            Now);
        var second = _factory.Create(
            message,
            NotificationKind.ReviewAssigned,
            Content(),
            preference: null,
            Now.AddMinutes(1));

        Assert.Equal(
            first.Deliveries.Select(item => item.MessageId),
            second.Deliveries.Select(item => item.MessageId));
        Assert.Equal(2, first.Deliveries.Select(item => item.MessageId).Distinct().Count());
        Assert.DoesNotContain(first.Deliveries, item => item.MessageId == Guid.Empty);
    }

    [Theory]
    [InlineData("https://example.test/path")]
    [InlineData("//example.test/path")]
    public void Create_ExternalOrProtocolRelativeDestination_IsRejected(string destination)
    {
        Assert.Throws<ArgumentException>(() => _factory.Create(
            Message(NotificationKind.ReviewAssigned),
            NotificationKind.ReviewAssigned,
            Content() with { DestinationPath = destination },
            preference: null,
            Now));
    }

    private static UserNotificationRequestedV1 Message(NotificationKind kind) => new(
        Guid.Parse("a9202b59-d6e8-431d-b31f-0720442b82c7"),
        RecipientUserId: 17,
        kind.ToString(),
        "review_assignment",
        SourceId: 31,
        OccurredAt: Now.AddMinutes(-1));

    private static NotificationContent Content() => new(
        "새 리뷰 작업",
        "게임의 리뷰 작업이 배정되었습니다.",
        "/Reviews/Submit?assignmentId=31");
}
