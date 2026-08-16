using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Contracts;
using MassTransit;

namespace IndieFi.Web.Modules.Media.Infrastructure;

public sealed class MassTransitMediaDeletionPublisher : IMediaDeletionPublisher
{
    private readonly IPublishEndpoint _publishEndpoint;

    public MassTransitMediaDeletionPublisher(IPublishEndpoint publishEndpoint)
    {
        _publishEndpoint = publishEndpoint;
    }

    public Task StageAsync(
        Guid eventId,
        Guid mediaId,
        int revision,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(eventId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(mediaId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);

        return _publishEndpoint.Publish(
            new MediaDeletionRequestedV1(
                eventId,
                mediaId,
                revision,
                occurredAt.ToUniversalTime()),
            context => context.MessageId = eventId,
            cancellationToken);
    }
}
