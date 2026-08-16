using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Contracts;
using MassTransit;

namespace IndieFi.Web.Modules.Notifications.Infrastructure;

public sealed class MassTransitKeyDropNotificationFanoutPublisher :
    IKeyDropNotificationFanoutPublisher
{
    private readonly IPublishEndpoint _publishEndpoint;

    public MassTransitKeyDropNotificationFanoutPublisher(
        IPublishEndpoint publishEndpoint)
    {
        _publishEndpoint = publishEndpoint;
    }

    public Task StageFavoriteGameCreatedAsync(
        Guid fanoutId,
        long dropId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(fanoutId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dropId);
        return _publishEndpoint.Publish(
            new FavoriteGameKeyDropCreatedFanoutV1(
                fanoutId,
                dropId,
                AfterUserId: 0,
                occurredAt.ToUniversalTime()),
            context => context.MessageId = fanoutId,
            cancellationToken);
    }

    public Task StageSubscribedDropOpenedAsync(
        Guid fanoutId,
        long dropId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(fanoutId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dropId);
        return _publishEndpoint.Publish(
            new SubscribedKeyDropOpenedFanoutV1(
                fanoutId,
                dropId,
                AfterUserId: 0,
                occurredAt.ToUniversalTime()),
            context => context.MessageId = fanoutId,
            cancellationToken);
    }
}
