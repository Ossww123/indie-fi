using IndieFi.Web.Data;
using IndieFi.Web.Modules.KeyDistributions.Domain;
using IndieFi.Web.Modules.KeyDistributions.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IndieFi.Web.Modules.KeyDistributions.Application;

public enum WaitingRoomParticipationOutcome
{
    Available,
    Joined,
    Existing,
    Rejoined,
    Completed,
    NotYetOpen,
    NotEligible,
    SoldOut,
    ClosedEarly,
    DropNotFound,
    AccountUnavailable,
    Terminal,
    Unavailable,
}

public sealed record WaitingRoomParticipationResult(
    WaitingRoomParticipationOutcome Outcome,
    WaitingRoomStoredEntry? Entry = null,
    WaitingRoomSnapshot? Snapshot = null)
{
    public bool CanJoin => Outcome == WaitingRoomParticipationOutcome.Available;
}

public sealed class WaitingRoomParticipationService
{
    private readonly AppDbContext _db;
    private readonly KeyDropEligibilityService _eligibilityService;
    private readonly IWaitingRoomStore _store;
    private readonly IWaitingRoomNotifier _notifier;
    private readonly IWaitingRoomLifecycle _waitingRoomLifecycle;
    private readonly WaitingRoomPolicyOptions _policy;

    public WaitingRoomParticipationService(
        AppDbContext db,
        KeyDropEligibilityService eligibilityService,
        IWaitingRoomStore store,
        IWaitingRoomNotifier notifier,
        IWaitingRoomLifecycle waitingRoomLifecycle,
        IOptions<WaitingRoomPolicyOptions> policy)
    {
        _db = db;
        _eligibilityService = eligibilityService;
        _store = store;
        _notifier = notifier;
        _waitingRoomLifecycle = waitingRoomLifecycle;
        _policy = policy.Value;
    }

    public async Task<WaitingRoomParticipationResult> GetAsync(
        long dropId,
        long userId,
        CancellationToken cancellationToken = default)
    {
        var evaluation = await EvaluateGateAsync(dropId, userId, cancellationToken);
        if (evaluation.Result.Outcome != WaitingRoomParticipationOutcome.Available)
        {
            return evaluation.Result;
        }

        var finalization = await FinalizeIfOpenAsync(
            dropId,
            evaluation.State!,
            cancellationToken);
        if (finalization is not null)
        {
            return finalization;
        }

        try
        {
            var admission = await AdvanceAdmissionsIfOpenAsync(
                dropId,
                evaluation.State!,
                cancellationToken);
            if (admission is not null)
            {
                return admission;
            }

            var snapshot = await _store.GetSnapshotAsync(
                dropId,
                userId,
                cancellationToken);
            return snapshot is null
                ? evaluation.Result
                : new WaitingRoomParticipationResult(
                    WaitingRoomParticipationOutcome.Existing,
                    snapshot.Entry,
                    snapshot);
        }
        catch (WaitingRoomUnavailableException)
        {
            return new WaitingRoomParticipationResult(
                WaitingRoomParticipationOutcome.Unavailable);
        }
    }

    public async Task<WaitingRoomParticipationResult> JoinAsync(
        long dropId,
        long userId,
        CancellationToken cancellationToken = default)
    {
        var evaluation = await EvaluateGateAsync(dropId, userId, cancellationToken);
        if (evaluation.Result.Outcome != WaitingRoomParticipationOutcome.Available)
        {
            return evaluation.Result;
        }

        var state = evaluation.State!;
        var mode = state.DatabaseNow < state.OpensAt
            ? WaitingRoomJoinMode.PreOpen
            : WaitingRoomJoinMode.Open;
        var finalization = await FinalizeIfOpenAsync(
            dropId,
            state,
            cancellationToken);
        if (finalization is not null)
        {
            return finalization;
        }
        try
        {
            var joined = await _store.JoinAsync(
                new WaitingRoomJoinCommand(dropId, userId, mode),
                cancellationToken);
            if (joined.Outcome == WaitingRoomJoinOutcome.Terminal)
            {
                return new WaitingRoomParticipationResult(
                    WaitingRoomParticipationOutcome.Terminal);
            }

            var admission = await AdvanceAdmissionsIfOpenAsync(
                dropId,
                state,
                cancellationToken);
            if (admission is not null)
            {
                return admission;
            }

            var snapshot = await _store.GetSnapshotAsync(
                dropId,
                userId,
                cancellationToken);
            var outcome = joined.Outcome switch
            {
                WaitingRoomJoinOutcome.Created => WaitingRoomParticipationOutcome.Joined,
                WaitingRoomJoinOutcome.Existing => WaitingRoomParticipationOutcome.Existing,
                WaitingRoomJoinOutcome.Rejoined => WaitingRoomParticipationOutcome.Rejoined,
                _ => throw new InvalidOperationException(
                    $"Unsupported waiting-room join outcome: {joined.Outcome}"),
            };
            if (joined.Outcome is WaitingRoomJoinOutcome.Created
                or WaitingRoomJoinOutcome.Rejoined)
            {
                await _notifier.NotifyDropChangedAsync(dropId, cancellationToken);
                await _notifier.NotifyUsersChangedAsync(
                    dropId,
                    [userId],
                    cancellationToken);
            }

            return new WaitingRoomParticipationResult(
                outcome,
                snapshot?.Entry ?? joined.Entry,
                snapshot);
        }
        catch (WaitingRoomUnavailableException)
        {
            return new WaitingRoomParticipationResult(
                WaitingRoomParticipationOutcome.Unavailable);
        }
    }

    private async Task<GateEvaluation> EvaluateGateAsync(
        long dropId,
        long userId,
        CancellationToken cancellationToken)
    {
        var state = await ReadDropStateAsync(dropId, userId, cancellationToken);
        if (state is null)
        {
            return new GateEvaluation(
                new WaitingRoomParticipationResult(
                    WaitingRoomParticipationOutcome.DropNotFound),
                null);
        }

        var eligibility = await _eligibilityService.EvaluateAsync(
            dropId,
            userId,
            cancellationToken);
        if (eligibility == KeyDropEligibilityOutcome.AccountUnavailable)
        {
            return Result(WaitingRoomParticipationOutcome.AccountUnavailable, state);
        }

        if (eligibility != KeyDropEligibilityOutcome.Eligible)
        {
            return Result(WaitingRoomParticipationOutcome.NotEligible, state);
        }

        var status = KeyDropStatusPolicy.Resolve(
            state.OpensAt,
            state.TotalKeys,
            state.RemainingKeys,
            state.ClosedAt,
            state.DatabaseNow);
        if (status == KeyDropStatus.SoldOut)
        {
            await _waitingRoomLifecycle.SynchronizeTerminalAsync(
                dropId,
                cancellationToken);
            if (state.AlreadyClaimed)
            {
                return Result(WaitingRoomParticipationOutcome.Completed, state);
            }

            return Result(WaitingRoomParticipationOutcome.SoldOut, state);
        }

        if (status == KeyDropStatus.ClosedEarly)
        {
            await _waitingRoomLifecycle.SynchronizeTerminalAsync(
                dropId,
                cancellationToken);
            if (state.AlreadyClaimed)
            {
                return Result(WaitingRoomParticipationOutcome.Completed, state);
            }

            return Result(WaitingRoomParticipationOutcome.ClosedEarly, state);
        }

        if (state.AlreadyClaimed)
        {
            return Result(WaitingRoomParticipationOutcome.Completed, state);
        }

        if (status == KeyDropStatus.Scheduled
            && state.DatabaseNow < state.OpensAt - _policy.OpensBefore)
        {
            return Result(WaitingRoomParticipationOutcome.NotYetOpen, state);
        }

        return Result(WaitingRoomParticipationOutcome.Available, state);
    }

    private async Task<WaitingRoomParticipationResult?> FinalizeIfOpenAsync(
        long dropId,
        DropState state,
        CancellationToken cancellationToken)
    {
        if (state.DatabaseNow < state.OpensAt)
        {
            return null;
        }

        try
        {
            var result = await _store.FinalizeOrderAsync(dropId, cancellationToken);
            if (result.Outcome == WaitingRoomOrderFinalizationOutcome.Finalized)
            {
                await _notifier.NotifyDropChangedAsync(dropId, cancellationToken);
            }

            return result.Outcome == WaitingRoomOrderFinalizationOutcome.Terminal
                ? new WaitingRoomParticipationResult(
                    WaitingRoomParticipationOutcome.Terminal)
                : null;
        }
        catch (WaitingRoomUnavailableException)
        {
            return new WaitingRoomParticipationResult(
                WaitingRoomParticipationOutcome.Unavailable);
        }
    }

    private async Task<WaitingRoomParticipationResult?> AdvanceAdmissionsIfOpenAsync(
        long dropId,
        DropState state,
        CancellationToken cancellationToken)
    {
        if (state.DatabaseNow < state.OpensAt)
        {
            return null;
        }

        var result = await _store.AdvanceAdmissionsAsync(dropId, cancellationToken);
        var changedUserIds = result.PromotedUserIds
            .Concat(result.ExpiredUserIds)
            .Distinct()
            .ToArray();
        if (changedUserIds.Length > 0)
        {
            await _notifier.NotifyDropChangedAsync(dropId, cancellationToken);
            await _notifier.NotifyUsersChangedAsync(
                dropId,
                changedUserIds,
                cancellationToken);
        }

        return result.Outcome == WaitingRoomAdmissionAdvanceOutcome.Terminal
            ? new WaitingRoomParticipationResult(
                WaitingRoomParticipationOutcome.Terminal)
            : null;
    }

    private static GateEvaluation Result(
        WaitingRoomParticipationOutcome outcome,
        DropState state) =>
        new(new WaitingRoomParticipationResult(outcome), state);

    private async Task<DropState?> ReadDropStateAsync(
        long dropId,
        long userId,
        CancellationToken cancellationToken)
    {
        var rows = await _db.Database
            .SqlQuery<DropState>($$"""
                SELECT key_drop.opens_at AS "OpensAt",
                       key_drop.total_keys AS "TotalKeys",
                       (
                           SELECT COUNT(*)::integer
                           FROM game_keys AS game_key
                           WHERE game_key.drop_id = key_drop.id
                             AND NOT EXISTS (
                                 SELECT 1
                                 FROM key_grants AS grant_record
                                 WHERE grant_record.key_id = game_key.id
                                   AND grant_record.drop_id = key_drop.id)
                       ) AS "RemainingKeys",
                       key_drop.closed_at AS "ClosedAt",
                       EXISTS (
                           SELECT 1
                           FROM key_grants AS grant_record
                           WHERE grant_record.drop_id = key_drop.id
                             AND grant_record.user_id = {{userId}}
                       ) AS "AlreadyClaimed",
                       now() AS "DatabaseNow"
                FROM key_drops AS key_drop
                WHERE key_drop.id = {{dropId}}
                """)
            .ToListAsync(cancellationToken);
        return rows.SingleOrDefault();
    }

    private sealed record DropState
    {
        public DateTimeOffset OpensAt { get; init; }

        public int TotalKeys { get; init; }

        public int RemainingKeys { get; init; }

        public DateTimeOffset? ClosedAt { get; init; }

        public bool AlreadyClaimed { get; init; }

        public DateTimeOffset DatabaseNow { get; init; }
    }

    private sealed record GateEvaluation(
        WaitingRoomParticipationResult Result,
        DropState? State);
}
