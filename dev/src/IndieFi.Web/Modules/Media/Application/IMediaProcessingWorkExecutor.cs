namespace IndieFi.Web.Modules.Media.Application;

public interface IMediaProcessingWorkExecutor
{
    Task ExecuteAsync(
        Guid mediaId,
        int revision,
        CancellationToken cancellationToken = default);
}
