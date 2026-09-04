using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Profiles.Application;

public enum UserFollowOutcome
{
    Following,
    NotFollowing,
    CannotFollowSelf,
    TargetNotFound,
    AccountUnavailable,
}

public sealed record UserFollowResult(
    UserFollowOutcome Outcome,
    bool Changed = false);

public sealed record FollowingListItem(
    long UserId,
    string DisplayName,
    string Biography,
    DateTimeOffset FollowedAt);

public sealed class UserFollowService
{
    private readonly AppDbContext _db;

    public UserFollowService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<bool> IsFollowingAsync(
        long followerId,
        long followedUserId,
        CancellationToken cancellationToken = default)
    {
        ValidateUserIds(followerId, followedUserId);

        return await _db.UserFollows
            .AsNoTracking()
            .AnyAsync(
                follow => follow.FollowerId == followerId
                    && follow.FollowedUserId == followedUserId,
                cancellationToken);
    }

    public Task<UserFollowResult> FollowAsync(
        long followerId,
        long followedUserId,
        CancellationToken cancellationToken = default) =>
        ChangeAsync(followerId, followedUserId, follow: true, cancellationToken);

    public Task<UserFollowResult> UnfollowAsync(
        long followerId,
        long followedUserId,
        CancellationToken cancellationToken = default) =>
        ChangeAsync(followerId, followedUserId, follow: false, cancellationToken);

    public async Task<IReadOnlyList<FollowingListItem>> GetFollowingAsync(
        long followerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(followerId);

        var rows = await (
                from follow in _db.UserFollows.AsNoTracking()
                join user in _db.Users.AsNoTracking()
                    on follow.FollowedUserId equals user.Id
                join profile in _db.UserProfiles.AsNoTracking()
                    on user.Id equals profile.UserId into profileRows
                from profile in profileRows.DefaultIfEmpty()
                where follow.FollowerId == followerId
                orderby follow.CreatedAt descending, follow.FollowedUserId descending
                select new
                {
                    user.Id,
                    Nickname = profile == null ? null : profile.Nickname,
                    Biography = profile == null ? string.Empty : profile.Biography,
                    follow.CreatedAt,
                })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new FollowingListItem(
                row.Id,
                UserDisplayName.Resolve(row.Id, row.Nickname),
                row.Biography,
                row.CreatedAt))
            .ToList();
    }

    private async Task<UserFollowResult> ChangeAsync(
        long followerId,
        long followedUserId,
        bool follow,
        CancellationToken cancellationToken)
    {
        ValidateUserIds(followerId, followedUserId);
        if (followerId == followedUserId)
        {
            return new UserFollowResult(UserFollowOutcome.CannotFollowSelf);
        }

        await using var transaction =
            await AccountMutationGuard.BeginTransactionIfNeededAsync(
                _db,
                cancellationToken);
        if (!await AccountMutationGuard.LockActiveAsync(
                _db,
                followerId,
                cancellationToken))
        {
            await AccountMutationGuard.CommitIfOwnedAsync(transaction, cancellationToken);
            return new UserFollowResult(UserFollowOutcome.AccountUnavailable);
        }

        if (!await _db.Users
                .AsNoTracking()
                .AnyAsync(user => user.Id == followedUserId, cancellationToken))
        {
            await AccountMutationGuard.CommitIfOwnedAsync(transaction, cancellationToken);
            return new UserFollowResult(UserFollowOutcome.TargetNotFound);
        }

        var changedRows = follow
            ? await _db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO user_follows (follower_id, followed_user_id)
                VALUES ({followerId}, {followedUserId})
                ON CONFLICT (follower_id, followed_user_id) DO NOTHING
                """,
                cancellationToken)
            : await _db.UserFollows
                .Where(item => item.FollowerId == followerId
                    && item.FollowedUserId == followedUserId)
                .ExecuteDeleteAsync(cancellationToken);

        await AccountMutationGuard.CommitIfOwnedAsync(transaction, cancellationToken);
        return new UserFollowResult(
            follow ? UserFollowOutcome.Following : UserFollowOutcome.NotFollowing,
            changedRows == 1);
    }

    private static void ValidateUserIds(long followerId, long followedUserId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(followerId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(followedUserId);
    }
}
