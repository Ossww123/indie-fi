using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Profiles.Application;

public enum FavoriteGameOutcome
{
    Favorited,
    NotFavorited,
    GameNotFound,
    AccountUnavailable,
}

public sealed record FavoriteGameResult(
    FavoriteGameOutcome Outcome,
    bool Changed = false);

public sealed record FavoriteGameListItem(
    long GameId,
    string Title,
    string? Description,
    long OwnerId,
    string OwnerDisplayName,
    DateTimeOffset FavoritedAt);

public sealed class FavoriteGameService
{
    private readonly AppDbContext _db;

    public FavoriteGameService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<bool> IsFavoriteAsync(
        long userId,
        long gameId,
        CancellationToken cancellationToken = default)
    {
        ValidateIds(userId, gameId);

        return await _db.FavoriteGames
            .AsNoTracking()
            .AnyAsync(
                favorite => favorite.UserId == userId
                    && favorite.GameId == gameId,
                cancellationToken);
    }

    public Task<FavoriteGameResult> FavoriteAsync(
        long userId,
        long gameId,
        CancellationToken cancellationToken = default) =>
        ChangeAsync(userId, gameId, favorite: true, cancellationToken);

    public Task<FavoriteGameResult> UnfavoriteAsync(
        long userId,
        long gameId,
        CancellationToken cancellationToken = default) =>
        ChangeAsync(userId, gameId, favorite: false, cancellationToken);

    public async Task<IReadOnlyList<FavoriteGameListItem>> GetFavoritesAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);

        var rows = await (
                from favorite in _db.FavoriteGames.AsNoTracking()
                join game in _db.Games.AsNoTracking()
                    on favorite.GameId equals game.Id
                join profile in _db.UserProfiles.AsNoTracking()
                    on game.OwnerId equals profile.UserId into profileRows
                from profile in profileRows.DefaultIfEmpty()
                where favorite.UserId == userId
                orderby favorite.CreatedAt descending, favorite.GameId descending
                select new
                {
                    GameId = game.Id,
                    game.Title,
                    game.Description,
                    game.OwnerId,
                    Nickname = profile == null ? null : profile.Nickname,
                    favorite.CreatedAt,
                })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new FavoriteGameListItem(
                row.GameId,
                row.Title,
                row.Description,
                row.OwnerId,
                UserDisplayName.Resolve(row.OwnerId, row.Nickname),
                row.CreatedAt))
            .ToList();
    }

    private async Task<FavoriteGameResult> ChangeAsync(
        long userId,
        long gameId,
        bool favorite,
        CancellationToken cancellationToken)
    {
        ValidateIds(userId, gameId);

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
            return new FavoriteGameResult(FavoriteGameOutcome.AccountUnavailable);
        }

        if (!await _db.Games
                .AsNoTracking()
                .AnyAsync(game => game.Id == gameId, cancellationToken))
        {
            await AccountMutationGuard.CommitIfOwnedAsync(transaction, cancellationToken);
            return new FavoriteGameResult(FavoriteGameOutcome.GameNotFound);
        }

        var changedRows = favorite
            ? await _db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO favorite_games (user_id, game_id)
                VALUES ({userId}, {gameId})
                ON CONFLICT (user_id, game_id) DO NOTHING
                """,
                cancellationToken)
            : await _db.FavoriteGames
                .Where(item => item.UserId == userId && item.GameId == gameId)
                .ExecuteDeleteAsync(cancellationToken);

        await AccountMutationGuard.CommitIfOwnedAsync(transaction, cancellationToken);
        return new FavoriteGameResult(
            favorite ? FavoriteGameOutcome.Favorited : FavoriteGameOutcome.NotFavorited,
            changedRows == 1);
    }

    private static void ValidateIds(long userId, long gameId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gameId);
    }
}
