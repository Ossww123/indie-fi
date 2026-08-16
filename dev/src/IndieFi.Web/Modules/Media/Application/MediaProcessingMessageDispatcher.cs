using IndieFi.Web.Modules.Media.Contracts;

namespace IndieFi.Web.Modules.Media.Application;

public sealed class MediaProcessingMessageDispatcher
{
    private readonly MediaMessageAuthorityService _authority;
    private readonly IMediaProcessingWorkExecutor _executor;

    public MediaProcessingMessageDispatcher(
        MediaMessageAuthorityService authority,
        IMediaProcessingWorkExecutor executor)
    {
        _authority = authority;
        _executor = executor;
    }

    public async Task<MediaMessageAuthorityOutcome> DispatchAsync(
        Guid? transportMessageId,
        MediaProcessingRequestedV1 message,
        CancellationToken cancellationToken = default)
    {
        var outcome = await _authority.AuthorizeProcessingAsync(
            transportMessageId,
            message,
            cancellationToken);
        if (outcome == MediaMessageAuthorityOutcome.Accepted)
        {
            await _executor.ExecuteAsync(
                message.MediaId,
                message.Revision,
                cancellationToken);
        }

        return outcome;
    }
}
