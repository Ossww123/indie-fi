using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Contracts;
using MassTransit;

namespace IndieFi.Web.Modules.Notifications.Infrastructure;

public sealed class MassTransitSocialNotificationFanoutPublisher :
    ISocialNotificationFanoutPublisher
{
    private readonly IPublishEndpoint _publishEndpoint;

    public MassTransitSocialNotificationFanoutPublisher(
        IPublishEndpoint publishEndpoint)
    {
        _publishEndpoint = publishEndpoint;
    }

    public Task StageGamePublishedAsync(
        Guid fanoutId,
        long gameId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(fanoutId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gameId);
        return _publishEndpoint.Publish(
            new FollowedUserGamePublishedFanoutV1(
                fanoutId,
                gameId,
                AfterUserId: 0,
                occurredAt.ToUniversalTime()),
            context => context.MessageId = fanoutId,
            cancellationToken);
    }
}
