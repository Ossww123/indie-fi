namespace IndieFi.Web.Modules.Media.Contracts;

public sealed record MediaProcessingRequestedV1(
    Guid EventId,
    Guid MediaId,
    int Revision,
    DateTimeOffset OccurredAt);
