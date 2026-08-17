namespace IndieFi.Web.Modules.Media.Application;

public interface IMediaDeletionWorkExecutor
{
    Task ExecuteAsync(
        Guid mediaId,
        int revision,
        CancellationToken cancellationToken = default);
}
