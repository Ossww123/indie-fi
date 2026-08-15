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
    WaitingRoomStoredEntry? Entry = null)
{
    public bool CanJoin => Outcome == WaitingRoomParticipationOutcome.Available;
}

public sealed class WaitingRoomParticipationService
{
    private readonly AppDbContext _db;
    private readonly KeyDropEligibilityService _eligibilityService;
    private readonly IWaitingRoomStore _store;
    private readonly WaitingRoomPolicyOptions _policy;

    public WaitingRoomParticipationService(
        AppDbContext db,
        KeyDropEligibilityService eligibilityService,
        IWaitingRoomStore store,
        IOptions<WaitingRoomPolicyOptions> policy)
    {
        _db = db;
        _eligibilityService = eligibilityService;
        _store = store;
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
            var entry = await _store.GetEntryAsync(dropId, userId, cancellationToken);
            return entry is null
                ? evaluation.Result
                : new WaitingRoomParticipationResult(
                    WaitingRoomParticipationOutcome.Existing,
                    entry);
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
            return joined.Outcome switch
            {
                WaitingRoomJoinOutcome.Created => new(
                    WaitingRoomParticipationOutcome.Joined,
                    joined.Entry),
                WaitingRoomJoinOutcome.Existing => new(
                    WaitingRoomParticipationOutcome.Existing,
                    joined.Entry),
                WaitingRoomJoinOutcome.Rejoined => new(
                    WaitingRoomParticipationOutcome.Rejoined,
                    joined.Entry),
                WaitingRoomJoinOutcome.Terminal => new(
                    WaitingRoomParticipationOutcome.Terminal),
                _ => throw new InvalidOperationException(
                    $"Unsupported waiting-room join outcome: {joined.Outcome}"),
            };
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

        if (state.AlreadyClaimed)
        {
            return Result(WaitingRoomParticipationOutcome.Completed, state);
        }

        var status = KeyDropStatusPolicy.Resolve(
            state.OpensAt,
            state.TotalKeys,
            state.RemainingKeys,
            state.ClosedAt,
            state.DatabaseNow);
        if (status == KeyDropStatus.SoldOut)
        {
            return Result(WaitingRoomParticipationOutcome.SoldOut, state);
        }

        if (status == KeyDropStatus.ClosedEarly)
        {
            return Result(WaitingRoomParticipationOutcome.ClosedEarly, state);
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
