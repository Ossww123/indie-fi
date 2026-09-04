namespace IndieFi.Web.Modules.Media.Contracts;

public sealed record MediaDeletionRequestedV1(
    Guid EventId,
    Guid MediaId,
    int Revision,
    DateTimeOffset OccurredAt);
