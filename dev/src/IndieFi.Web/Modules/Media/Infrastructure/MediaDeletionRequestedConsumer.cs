using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Contracts;
using MassTransit;

namespace IndieFi.Web.Modules.Media.Infrastructure;

public sealed class MediaDeletionRequestedConsumer :
    IConsumer<MediaDeletionRequestedV1>
{
    private readonly MediaDeletionMessageDispatcher _dispatcher;
    private readonly ILogger<MediaDeletionRequestedConsumer> _logger;

    public MediaDeletionRequestedConsumer(
        MediaDeletionMessageDispatcher dispatcher,
        ILogger<MediaDeletionRequestedConsumer> logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<MediaDeletionRequestedV1> context)
    {
        var outcome = await _dispatcher.DispatchAsync(
            context.MessageId,
            context.Message,
            context.CancellationToken);

        _logger.LogInformation(
            "Media deletion request mediaId={MediaId} revision={Revision} outcome={Outcome}",
            context.Message.MediaId,
            context.Message.Revision,
            outcome);
    }
}
