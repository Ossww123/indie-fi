using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.KeyDistributions.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace IndieFi.Web.Modules.KeyDistributions.Application;

public enum ClaimOutcome
{
    Granted,
    AlreadyClaimed,
    NotOpen,
    SoldOut,
    ClosedEarly,
    Conflict,
    DropNotFound,
    AccountUnavailable,
    NotEligible,
    AdmissionRequired,
    WaitingRoomTerminal,
    WaitingRoomUnavailable,
}

public record ClaimResult(ClaimOutcome Outcome);

public class KeyClaimService
{
    private const string UniqueViolation = "23505";
    private const string OneKeyPerUserIndex = "IX_key_grants_drop_id_user_id";

    private readonly AppDbContext _db;
    private readonly KeyDropMutationLock _keyDropMutationLock;
    private readonly KeyDropEligibilityService _keyDropEligibilityService;
    private readonly IKeyClaimAdmissionGate _admissionGate;
    private readonly IWaitingRoomLifecycle _waitingRoomLifecycle;
    private readonly ILogger<KeyClaimService> _logger;

    public KeyClaimService(
        AppDbContext db,
        KeyDropMutationLock keyDropMutationLock,
        ILogger<KeyClaimService> logger,
        IKeyClaimAdmissionGate admissionGate,
        IWaitingRoomLifecycle waitingRoomLifecycle,
        KeyDropEligibilityService? keyDropEligibilityService = null)
    {
        _db = db;
        _keyDropMutationLock = keyDropMutationLock;
        _keyDropEligibilityService = keyDropEligibilityService
            ?? new KeyDropEligibilityService(db);
        _admissionGate = admissionGate;
        _waitingRoomLifecycle = waitingRoomLifecycle;
        _logger = logger;
    }

    public async Task<ClaimResult> ClaimAsync(
        long dropId,
        long userId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction =
            await AccountMutationGuard.BeginTransactionIfNeededAsync(
                _db,
                cancellationToken);
        var state = await _keyDropMutationLock.LockAsync(
            dropId,
            userId,
            cancellationToken);

        if (!state.AccountAvailable)
        {
            return await CompleteAsync(
                transaction,
                ClaimOutcome.AccountUnavailable,
                dropId,
                userId,
                cancellationToken);
        }

        if (!state.DropExists)
        {
            return await CompleteAsync(
                transaction,
                ClaimOutcome.DropNotFound,
                dropId,
                userId,
                cancellationToken);
        }

        if (state.AlreadyClaimed)
        {
            return await CompleteAsync(
                transaction,
                ClaimOutcome.AlreadyClaimed,
                dropId,
                userId,
                cancellationToken);
        }

        var status = state.GetRequiredStatus();
        if (status != KeyDropStatus.Active)
        {
            var outcome = status switch
            {
                KeyDropStatus.Scheduled => ClaimOutcome.NotOpen,
                KeyDropStatus.SoldOut => ClaimOutcome.SoldOut,
                KeyDropStatus.ClosedEarly => ClaimOutcome.ClosedEarly,
                _ => throw new InvalidOperationException(
                    $"Unsupported key drop status: {status}"),
            };
            return await CompleteAsync(
                transaction,
                outcome,
                dropId,
                userId,
                cancellationToken);
        }

        if (_keyDropEligibilityService.Evaluate(
                state.GetRequiredEligibilitySnapshot())
            != KeyDropEligibilityOutcome.Eligible)
        {
            return await CompleteAsync(
                transaction,
                ClaimOutcome.NotEligible,
                dropId,
                userId,
                cancellationToken);
        }

        var admission = await _admissionGate.ValidateAsync(
            dropId,
            userId,
            cancellationToken);
        if (admission != KeyClaimAdmissionOutcome.Allowed)
        {
            var outcome = admission switch
            {
                KeyClaimAdmissionOutcome.Required => ClaimOutcome.AdmissionRequired,
                KeyClaimAdmissionOutcome.Terminal => ClaimOutcome.WaitingRoomTerminal,
                KeyClaimAdmissionOutcome.Unavailable => ClaimOutcome.WaitingRoomUnavailable,
                _ => throw new InvalidOperationException(
                    $"Unsupported key-claim admission outcome: {admission}"),
            };
            return await CompleteAsync(
                transaction,
                outcome,
                dropId,
                userId,
                cancellationToken);
        }

        try
        {
            var claimMarkers = await _db.Database
                .SqlQuery<long>($$"""
                    WITH picked AS MATERIALIZED (
                        SELECT game_key.id
                        FROM game_keys AS game_key
                        WHERE game_key.drop_id = {{dropId}}
                          AND NOT EXISTS (
                              SELECT 1
                              FROM key_grants AS grant_record
                              WHERE grant_record.key_id = game_key.id)
                        ORDER BY game_key.id
                        FOR UPDATE SKIP LOCKED
                        LIMIT 1
                    ),
                    granted AS (
                        INSERT INTO key_grants (drop_id, user_id, key_id)
                        SELECT {{dropId}}, {{userId}}, picked.id
                        FROM picked
                        RETURNING key_id
                    ),
                    advanced AS (
                        UPDATE key_drops AS key_drop
                        SET state_version = key_drop.state_version + 1
                        FROM granted
                        WHERE key_drop.id = {{dropId}}
                          AND key_drop.state_version = {{state.StateVersion!.Value}}
                          AND key_drop.closed_at IS NULL
                        RETURNING key_drop.state_version
                    )
                    SELECT CASE
                        WHEN EXISTS (SELECT 1 FROM granted)
                         AND EXISTS (SELECT 1 FROM advanced)
                            THEN (SELECT key_id FROM granted)
                        WHEN EXISTS (SELECT 1 FROM granted) THEN -2
                        ELSE -1
                    END AS "Value"
                    """)
                .ToListAsync(cancellationToken);
            var claimMarker = claimMarkers.Single();

            if (claimMarker == -1)
            {
                return await CompleteAsync(
                    transaction,
                    ClaimOutcome.SoldOut,
                    dropId,
                    userId,
                    cancellationToken);
            }

            if (claimMarker == -2)
            {
                return await RollbackConflictAsync(
                    transaction,
                    dropId,
                    userId,
                    cancellationToken);
            }

            return await CompleteAsync(
                transaction,
                ClaimOutcome.Granted,
                dropId,
                userId,
                cancellationToken,
                synchronizeTerminal: state.RemainingKeys == 1);
        }
        catch (PostgresException exception)
            when (exception.SqlState == UniqueViolation)
        {
            if (transaction is null)
            {
                throw;
            }

            await AccountMutationGuard.RollbackIfOwnedAsync(
                transaction,
                cancellationToken);
            var outcome = exception.ConstraintName == OneKeyPerUserIndex
                ? ClaimOutcome.AlreadyClaimed
                : ClaimOutcome.Conflict;
            if (outcome == ClaimOutcome.AlreadyClaimed)
            {
                await ReflectCompletionBestEffortAsync(
                    dropId,
                    userId,
                    cancellationToken);
            }

            return Result(outcome, dropId, userId);
        }
    }

    private async Task<ClaimResult> CompleteAsync(
        IDbContextTransaction? transaction,
        ClaimOutcome outcome,
        long dropId,
        long userId,
        CancellationToken cancellationToken,
        bool synchronizeTerminal = false)
    {
        await AccountMutationGuard.CommitIfOwnedAsync(
            transaction,
            cancellationToken);
        if (synchronizeTerminal)
        {
            await _waitingRoomLifecycle.SynchronizeTerminalAsync(
                dropId,
                cancellationToken);
        }

        if (outcome is ClaimOutcome.Granted or ClaimOutcome.AlreadyClaimed)
        {
            await ReflectCompletionBestEffortAsync(
                dropId,
                userId,
                cancellationToken);
        }

        return Result(outcome, dropId, userId);
    }

    private async Task ReflectCompletionBestEffortAsync(
        long dropId,
        long userId,
        CancellationToken cancellationToken)
    {
        try
        {
            await _admissionGate.CompleteAsync(
                dropId,
                userId,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Waiting-room completion sync failed after PostgreSQL claim drop={DropId} user={UserId}",
                dropId,
                userId);
        }
    }

    private async Task<ClaimResult> RollbackConflictAsync(
        IDbContextTransaction? transaction,
        long dropId,
        long userId,
        CancellationToken cancellationToken)
    {
        if (transaction is null)
        {
            throw new DbUpdateConcurrencyException(
                "The key-drop state version changed while granting a key.");
        }

        await AccountMutationGuard.RollbackIfOwnedAsync(
            transaction,
            cancellationToken);
        return Result(ClaimOutcome.Conflict, dropId, userId);
    }

    // One structured line per claim. Under load the screen message (redirect +
    // one-shot TempData cookie) is an unreliable signal, so outcomes are counted here.
    private ClaimResult Result(ClaimOutcome outcome, long dropId, long userId)
    {
        _logger.LogInformation(
            "KeyClaim outcome={Outcome} drop={DropId} user={UserId}",
            outcome,
            dropId,
            userId);
        return new ClaimResult(outcome);
    }
}
