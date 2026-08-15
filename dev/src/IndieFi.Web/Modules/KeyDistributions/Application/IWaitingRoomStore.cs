using IndieFi.Web.Modules.KeyDistributions.Domain;

namespace IndieFi.Web.Modules.KeyDistributions.Application;

public interface IWaitingRoomStore
{
    Task<WaitingRoomJoinResult> JoinAsync(
        WaitingRoomJoinCommand command,
        CancellationToken cancellationToken = default);

    Task<WaitingRoomStoredEntry?> GetEntryAsync(
        long dropId,
        long userId,
        CancellationToken cancellationToken = default);

    Task<WaitingRoomSnapshot?> GetSnapshotAsync(
        long dropId,
        long userId,
        CancellationToken cancellationToken = default);

    Task<WaitingRoomTransitionResult> TransitionAsync(
        WaitingRoomTransitionCommand command,
        CancellationToken cancellationToken = default);

    Task<WaitingRoomOrderFinalizationResult> FinalizeOrderAsync(
        long dropId,
        CancellationToken cancellationToken = default);
}

public enum WaitingRoomJoinMode
{
    PreOpen,
    Open,
}

public sealed record WaitingRoomJoinCommand(
    long DropId,
    long UserId,
    WaitingRoomJoinMode Mode);

public enum WaitingRoomJoinOutcome
{
    Created,
    Existing,
    Rejoined,
    Terminal,
}

public sealed record WaitingRoomJoinResult(
    WaitingRoomJoinOutcome Outcome,
    WaitingRoomStoredEntry? Entry);

public sealed record WaitingRoomTransitionCommand(
    long DropId,
    long UserId,
    WaitingRoomEntryState ExpectedState,
    WaitingRoomEntryState TargetState);

public enum WaitingRoomTransitionOutcome
{
    Changed,
    Missing,
    StateMismatch,
    Terminal,
}

public sealed record WaitingRoomTransitionResult(
    WaitingRoomTransitionOutcome Outcome,
    WaitingRoomStoredEntry? Entry);

public enum WaitingRoomOrderFinalizationOutcome
{
    Finalized,
    AlreadyFinalized,
    Terminal,
}

public sealed record WaitingRoomOrderFinalizationResult(
    WaitingRoomOrderFinalizationOutcome Outcome,
    int WaitingCount);

public sealed record WaitingRoomSnapshot(
    WaitingRoomStoredEntry Entry,
    int? Position,
    int? AheadCount,
    int WaitingCount,
    int PreWaitingCount,
    int AdmittedCount,
    bool OrderFinalized);
