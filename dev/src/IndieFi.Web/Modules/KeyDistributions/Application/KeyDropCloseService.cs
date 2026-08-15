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

    public KeyDropCloseService(
        AppDbContext db,
        KeyDropMutationLock keyDropMutationLock)
    {
        _db = db;
        _keyDropMutationLock = keyDropMutationLock;
    }

    public async Task<CloseKeyDropResult> CloseAsync(
        long dropId,
        long ownerId,
        CancellationToken cancellationToken = default)
    {
        // Capture the version before waiting for the mutation lock. If a claim or
        // inventory add commits while this close request waits, closing must not
        // silently apply to that newer state.
        var expectedVersion = await _db.KeyDrops
            .AsNoTracking()
            .Where(drop => drop.Id == dropId)
            .Select(drop => (long?)drop.StateVersion)
            .SingleOrDefaultAsync(cancellationToken);

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

        if (expectedVersion is null
            || state.StateVersion != expectedVersion.Value)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new CloseKeyDropResult(CloseKeyDropOutcome.Conflict);
        }

        var updatedDropCount = await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE key_drops
            SET closed_at = now(),
                state_version = state_version + 1
            WHERE id = {dropId}
              AND state_version = {expectedVersion.Value}
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
        return new CloseKeyDropResult(CloseKeyDropOutcome.Closed);
    }
}
