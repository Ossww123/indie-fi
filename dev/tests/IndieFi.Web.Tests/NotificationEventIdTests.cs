using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Domain;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class NotificationEventIdTests
{
    [Fact]
    public void Create_SameOccurrence_ReturnsStableId()
    {
        var first = NotificationEventId.Create(
            NotificationKind.ReviewAssigned,
            sourceId: 41,
            recipientUserId: 73);
        var second = NotificationEventId.Create(
            NotificationKind.ReviewAssigned,
            sourceId: 41,
            recipientUserId: 73);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Create_DifferentRoutingIdentity_ReturnsDifferentIds()
    {
        var baseline = NotificationEventId.Create(
            NotificationKind.ReviewAssigned,
            sourceId: 41,
            recipientUserId: 73);

        Assert.NotEqual(
            baseline,
            NotificationEventId.Create(
                NotificationKind.ReviewExpired,
                sourceId: 41,
                recipientUserId: 73));
        Assert.NotEqual(
            baseline,
            NotificationEventId.Create(
                NotificationKind.ReviewAssigned,
                sourceId: 42,
                recipientUserId: 73));
        Assert.NotEqual(
            baseline,
            NotificationEventId.Create(
                NotificationKind.ReviewAssigned,
                sourceId: 41,
                recipientUserId: 74));
    }
}
