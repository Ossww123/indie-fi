using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Application;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IndieFi.Web.Modules.Notifications.Application;

public enum KeyDropSubscriptionOutcome
{
    Subscribed,
    Unsubscribed,
    DropNotFound,
    NotScheduled,
    AccountUnavailable,
}

public sealed record KeyDropSubscriptionResult(
    KeyDropSubscriptionOutcome Outcome,
    bool Changed = false);

public sealed class KeyDropSubscriptionService
{
    private const string DropStateSql = """
        SELECT id AS "DropId",
               opens_at AS "OpensAt",
               closed_at AS "ClosedAt",
               now() AS "DatabaseNow"
          FROM key_drops
         WHERE id = @dropId
        """;

    private readonly AppDbContext _db;

    public KeyDropSubscriptionService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<bool> IsSubscribedAsync(
        long userId,
        long dropId,
        CancellationToken cancellationToken = default)
    {
        ValidateIds(userId, dropId);
        return await _db.KeyDropSubscriptions
            .AsNoTracking()
            .AnyAsync(
                item => item.UserId == userId && item.DropId == dropId,
                cancellationToken);
    }

    public Task<KeyDropSubscriptionResult> SubscribeAsync(
        long userId,
        long dropId,
        CancellationToken cancellationToken = default) =>
        ChangeAsync(userId, dropId, subscribe: true, cancellationToken);

    public Task<KeyDropSubscriptionResult> UnsubscribeAsync(
        long userId,
        long dropId,
        CancellationToken cancellationToken = default) =>
        ChangeAsync(userId, dropId, subscribe: false, cancellationToken);

    private async Task<KeyDropSubscriptionResult> ChangeAsync(
        long userId,
        long dropId,
        bool subscribe,
        CancellationToken cancellationToken)
    {
        ValidateIds(userId, dropId);
        await using var transaction =
            await AccountMutationGuard.BeginTransactionIfNeededAsync(
                _db,
                cancellationToken);
        if (!await AccountMutationGuard.LockActiveAsync(
                _db,
                userId,
                cancellationToken))
        {
            await AccountMutationGuard.CommitIfOwnedAsync(transaction, cancellationToken);
            return new KeyDropSubscriptionResult(
                KeyDropSubscriptionOutcome.AccountUnavailable);
        }

        var drop = (await _db.Database
                .SqlQueryRaw<KeyDropSubscriptionState>(
                    DropStateSql,
                    new NpgsqlParameter("dropId", dropId))
                .ToListAsync(cancellationToken))
            .SingleOrDefault();
        if (drop is null)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(transaction, cancellationToken);
            return new KeyDropSubscriptionResult(
                KeyDropSubscriptionOutcome.DropNotFound);
        }

        if (drop.ClosedAt is not null || drop.OpensAt <= drop.DatabaseNow)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(transaction, cancellationToken);
            return new KeyDropSubscriptionResult(
                KeyDropSubscriptionOutcome.NotScheduled);
        }

        var changedRows = subscribe
            ? await _db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO key_drop_subscriptions (user_id, drop_id)
                VALUES ({userId}, {dropId})
                ON CONFLICT (user_id, drop_id) DO NOTHING
                """,
                cancellationToken)
            : await _db.KeyDropSubscriptions
                .Where(item => item.UserId == userId && item.DropId == dropId)
                .ExecuteDeleteAsync(cancellationToken);
        await AccountMutationGuard.CommitIfOwnedAsync(transaction, cancellationToken);
        return new KeyDropSubscriptionResult(
            subscribe
                ? KeyDropSubscriptionOutcome.Subscribed
                : KeyDropSubscriptionOutcome.Unsubscribed,
            Changed: changedRows == 1);
    }

    private static void ValidateIds(long userId, long dropId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dropId);
    }

    private sealed record KeyDropSubscriptionState(
        long DropId,
        DateTimeOffset OpensAt,
        DateTimeOffset? ClosedAt,
        DateTimeOffset DatabaseNow);
}
