namespace IndieFi.Web.Modules.Media.Application;

public interface IMediaDeletionPublisher
{
    Task StageAsync(
        Guid eventId,
        Guid mediaId,
        int revision,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);
}
