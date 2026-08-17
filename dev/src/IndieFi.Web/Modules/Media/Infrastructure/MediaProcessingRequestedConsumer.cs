using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Contracts;
using MassTransit;

namespace IndieFi.Web.Modules.Media.Infrastructure;

public sealed class MediaProcessingRequestedConsumer :
    IConsumer<MediaProcessingRequestedV1>
{
    private readonly MediaProcessingMessageDispatcher _dispatcher;
    private readonly ILogger<MediaProcessingRequestedConsumer> _logger;

    public MediaProcessingRequestedConsumer(
        MediaProcessingMessageDispatcher dispatcher,
        ILogger<MediaProcessingRequestedConsumer> logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<MediaProcessingRequestedV1> context)
    {
        var outcome = await _dispatcher.DispatchAsync(
            context.MessageId,
            context.Message,
            context.CancellationToken);

        _logger.LogInformation(
            "Media processing request mediaId={MediaId} revision={Revision} outcome={Outcome}",
            context.Message.MediaId,
            context.Message.Revision,
            outcome);
    }
}
