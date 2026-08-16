using IndieFi.Web.Modules.Media.Contracts;

namespace IndieFi.Web.Modules.Media.Application;

public sealed class MediaDeletionMessageDispatcher
{
    private readonly MediaMessageAuthorityService _authority;
    private readonly IMediaDeletionWorkExecutor _executor;

    public MediaDeletionMessageDispatcher(
        MediaMessageAuthorityService authority,
        IMediaDeletionWorkExecutor executor)
    {
        _authority = authority;
        _executor = executor;
    }

    public async Task<MediaMessageAuthorityOutcome> DispatchAsync(
        Guid? transportMessageId,
        MediaDeletionRequestedV1 message,
        CancellationToken cancellationToken = default)
    {
        var outcome = await _authority.AuthorizeDeletionAsync(
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
