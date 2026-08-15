using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.KeyDistributions.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.KeyDistributions.Application;

public enum CloseKeyDropOutcome
{
    Closed,
    AlreadyClosed,
    DropNotFound,
    NotOwner,
    InvalidStatus,
    Conflict,
    AccountUnavailable,
}

public sealed record CloseKeyDropResult(CloseKeyDropOutcome Outcome);

public sealed class KeyDropCloseService
{
    private readonly AppDbContext _db;
    private readonly KeyDropMutationLock _keyDropMutationLock;
    private readonly IWaitingRoomLifecycle _waitingRoomLifecycle;

    public KeyDropCloseService(
        AppDbContext db,
        KeyDropMutationLock keyDropMutationLock,
        IWaitingRoomLifecycle waitingRoomLifecycle)
    {
        _db = db;
        _keyDropMutationLock = keyDropMutationLock;
        _waitingRoomLifecycle = waitingRoomLifecycle;
    }

    public async Task<CloseKeyDropResult> CloseAsync(
        long dropId,
        long ownerId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction =
            await AccountMutationGuard.BeginTransactionIfNeededAsync(
                _db,
                cancellationToken);
        var state = await _keyDropMutationLock.LockAsync(
            dropId,
            ownerId,
            cancellationToken);

        if (!state.AccountAvailable)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new CloseKeyDropResult(
                CloseKeyDropOutcome.AccountUnavailable);
        }

        if (!state.DropExists)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new CloseKeyDropResult(CloseKeyDropOutcome.DropNotFound);
        }

        if (state.OwnerId != ownerId)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new CloseKeyDropResult(CloseKeyDropOutcome.NotOwner);
        }

        var status = state.GetRequiredStatus();
        if (status == KeyDropStatus.ClosedEarly)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new CloseKeyDropResult(CloseKeyDropOutcome.AlreadyClosed);
        }

        if (!KeyDropStatusPolicy.CanCloseEarly(status))
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new CloseKeyDropResult(CloseKeyDropOutcome.InvalidStatus);
        }

        // The locked state is authoritative, so the version guard uses it rather
        // than an earlier snapshot. A claim or inventory add that committed before
        // the lock was acquired does not invalidate the owner's intent to close:
        // if it drained the last key, the CanCloseEarly check above already
        // rejected the request. The guard still serializes concurrent closes.
        var updatedDropCount = await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE key_drops
            SET closed_at = now(),
                state_version = state_version + 1
            WHERE id = {dropId}
              AND state_version = {state.StateVersion!.Value}
              AND closed_at IS NULL
            """,
            cancellationToken);
        if (updatedDropCount != 1)
        {
            await AccountMutationGuard.RollbackIfOwnedAsync(
                transaction,
                cancellationToken);
            return new CloseKeyDropResult(CloseKeyDropOutcome.Conflict);
        }

        await AccountMutationGuard.CommitIfOwnedAsync(
            transaction,
            cancellationToken);
        await _waitingRoomLifecycle.SynchronizeTerminalAsync(
            dropId,
            cancellationToken);
        return new CloseKeyDropResult(CloseKeyDropOutcome.Closed);
    }
}
