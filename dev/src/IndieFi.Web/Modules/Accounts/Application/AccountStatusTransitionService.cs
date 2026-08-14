using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IndieFi.Web.Modules.Accounts.Application;

public enum AccountStatusTransitionOutcome
{
    Changed,
    Unchanged,
    UserNotFound,
    InvalidTemporarySuspensionEnd,
    NotEligible,
}

public sealed record AccountStatusTransitionResult(
    AccountStatusTransitionOutcome Outcome,
    AccountStatus? Status = null,
    DateTimeOffset? SuspendedUntil = null,
    long? SessionVersion = null,
    int ExpiredReviewAssignmentCount = 0);

public sealed class AccountStatusTransitionService
{
    // Review submission locks the assignment before its credit trigger reaches the user
    // row. Taking the same order here avoids an opposite-order deadlock. The later update
    // rechecks Active so a lease sweep or completed submission remains the single winner.
    private const string LockActiveReviewAssignmentsSql = """
        SELECT id AS "Value"
          FROM review_assignments
         WHERE reviewer_id = @reviewerId
           AND status = 'Active'
         ORDER BY id
         FOR UPDATE
        """;

    private const string ExpireActiveReviewAssignmentsSql = """
        WITH expired AS (
            UPDATE review_assignments
               SET status = 'Expired'
             WHERE reviewer_id = @reviewerId
               AND status = 'Active'
            RETURNING request_id
        ),
        requeued AS (
            UPDATE review_requests
               SET status = 'Queued'
              FROM expired
             WHERE review_requests.id = expired.request_id
               AND review_requests.status = 'Assigned'
            RETURNING review_requests.id
        )
        SELECT count(*)::integer AS "Value" FROM expired
        """;

    private readonly AppDbContext _db;
    private readonly TimeProvider _timeProvider;

    public AccountStatusTransitionService(
        AppDbContext db,
        TimeProvider timeProvider)
    {
        _db = db;
        _timeProvider = timeProvider;
    }

    public Task<AccountStatusTransitionResult> SuspendTemporarilyAsync(
        long userId,
        DateTimeOffset suspendedUntil,
        CancellationToken cancellationToken = default)
    {
        return TransitionAsync(
            userId,
            TransitionKind.SuspendTemporarily,
            suspendedUntil.ToUniversalTime(),
            cancellationToken);
    }

    public Task<AccountStatusTransitionResult> SuspendIndefinitelyAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        return TransitionAsync(
            userId,
            TransitionKind.SuspendIndefinitely,
            suspendedUntil: null,
            cancellationToken);
    }

    public Task<AccountStatusTransitionResult> ReactivateAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        return TransitionAsync(
            userId,
            TransitionKind.Reactivate,
            suspendedUntil: null,
            cancellationToken);
    }

    public Task<AccountStatusTransitionResult> ReactivateExpiredTemporaryAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        return TransitionAsync(
            userId,
            TransitionKind.ReactivateExpiredTemporary,
            suspendedUntil: null,
            cancellationToken);
    }

    private async Task<AccountStatusTransitionResult> TransitionAsync(
        long userId,
        TransitionKind transition,
        DateTimeOffset? suspendedUntil,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);

        await using var transaction = await _db.Database.BeginTransactionAsync(
            cancellationToken);
        var suspending = transition is TransitionKind.SuspendTemporarily
            or TransitionKind.SuspendIndefinitely;
        if (suspending)
        {
            await _db.Database
                .SqlQueryRaw<long>(
                    LockActiveReviewAssignmentsSql,
                    new NpgsqlParameter("reviewerId", userId))
                .ToListAsync(cancellationToken);
        }

        var users = await _db.Users
            .FromSqlInterpolated(
                $"SELECT * FROM users WHERE id = {userId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        var user = users.SingleOrDefault();

        if (user is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new AccountStatusTransitionResult(
                AccountStatusTransitionOutcome.UserNotFound);
        }

        var now = _timeProvider.GetUtcNow();
        if (transition == TransitionKind.SuspendTemporarily
            && (suspendedUntil is null || suspendedUntil <= now))
        {
            await transaction.CommitAsync(cancellationToken);
            return Current(
                AccountStatusTransitionOutcome.InvalidTemporarySuspensionEnd,
                user);
        }

        if (transition == TransitionKind.ReactivateExpiredTemporary)
        {
            var accessState = AccountStatusPolicy.Evaluate(
                user.AccountStatus,
                user.SuspendedUntil,
                now);
            if (accessState == AccountAccessState.Active)
            {
                await transaction.CommitAsync(cancellationToken);
                return Current(AccountStatusTransitionOutcome.Unchanged, user);
            }

            if (accessState != AccountAccessState.TemporarySuspensionExpired)
            {
                await transaction.CommitAsync(cancellationToken);
                return Current(AccountStatusTransitionOutcome.NotEligible, user);
            }
        }

        var targetStatus = transition == TransitionKind.SuspendTemporarily
            ? AccountStatus.TemporarilySuspended
            : transition == TransitionKind.SuspendIndefinitely
                ? AccountStatus.IndefinitelySuspended
                : AccountStatus.Active;
        var targetSuspendedUntil = targetStatus == AccountStatus.TemporarilySuspended
            ? suspendedUntil
            : null;

        var expiredReviewAssignmentCount = suspending
            ? (await _db.Database
                .SqlQueryRaw<int>(
                    ExpireActiveReviewAssignmentsSql,
                    new NpgsqlParameter("reviewerId", userId))
                .ToListAsync(cancellationToken))[0]
            : 0;

        if (user.AccountStatus == targetStatus
            && user.SuspendedUntil == targetSuspendedUntil)
        {
            await transaction.CommitAsync(cancellationToken);
            return Current(
                AccountStatusTransitionOutcome.Unchanged,
                user,
                expiredReviewAssignmentCount);
        }

        ApplyTransition(user, targetStatus, targetSuspendedUntil);

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Current(
            AccountStatusTransitionOutcome.Changed,
            user,
            expiredReviewAssignmentCount);
    }

    internal static void ApplyTransition(
        User user,
        AccountStatus targetStatus,
        DateTimeOffset? suspendedUntil)
    {
        ArgumentNullException.ThrowIfNull(user);

        var targetSuspendedUntil = targetStatus == AccountStatus.TemporarilySuspended
            ? suspendedUntil
            : null;
        AccountStatusPolicy.ValidateShape(targetStatus, targetSuspendedUntil);

        var nextSessionVersion = checked(user.SessionVersion + 1);
        user.AccountStatus = targetStatus;
        user.SuspendedUntil = targetSuspendedUntil;
        user.SessionVersion = nextSessionVersion;
    }

    private static AccountStatusTransitionResult Current(
        AccountStatusTransitionOutcome outcome,
        User user,
        int expiredReviewAssignmentCount = 0)
    {
        return new AccountStatusTransitionResult(
            outcome,
            user.AccountStatus,
            user.SuspendedUntil,
            user.SessionVersion,
            expiredReviewAssignmentCount);
    }

    private enum TransitionKind
    {
        SuspendTemporarily,
        SuspendIndefinitely,
        Reactivate,
        ReactivateExpiredTemporary,
    }
}
