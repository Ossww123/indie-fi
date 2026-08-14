using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace IndieFi.Web.Modules.Accounts.Application;

internal static class AccountMutationGuard
{
    public static async Task<IDbContextTransaction?> BeginTransactionIfNeededAsync(
        AppDbContext db,
        CancellationToken cancellationToken = default)
    {
        return db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
    }

    public static Task CommitIfOwnedAsync(
        IDbContextTransaction? transaction,
        CancellationToken cancellationToken = default)
    {
        return transaction is null
            ? Task.CompletedTask
            : transaction.CommitAsync(cancellationToken);
    }

    public static Task RollbackIfOwnedAsync(
        IDbContextTransaction? transaction,
        CancellationToken cancellationToken = default)
    {
        return transaction is null
            ? Task.CompletedTask
            : transaction.RollbackAsync(cancellationToken);
    }

    public static async Task<bool> LockActiveAsync(
        AppDbContext db,
        long userId,
        CancellationToken cancellationToken = default)
    {
        var users = await db.Users
            .FromSqlInterpolated(
                $"SELECT * FROM users WHERE id = {userId} FOR UPDATE")
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var user = users.SingleOrDefault();
        return user is not null
            && AccountStatusPolicy.AllowsMutation(
                user.AccountStatus,
                user.SuspendedUntil);
    }
}
