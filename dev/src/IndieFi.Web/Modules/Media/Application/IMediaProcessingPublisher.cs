namespace IndieFi.Web.Modules.Media.Application;

public interface IMediaProcessingPublisher
{
    Task StageAsync(
        Guid eventId,
        Guid mediaId,
        int revision,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);
}
