using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Media.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Media.Application;

public enum GameScreenshotOrderOutcome
{
    Reordered,
    AccountUnavailable,
    GameNotFound,
    InvalidOrder,
}

public sealed record GameScreenshotOrderResult(
    GameScreenshotOrderOutcome Outcome,
    IReadOnlyList<Guid>? MediaIds = null);

public sealed class GameScreenshotOrderService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _timeProvider;

    public GameScreenshotOrderService(
        AppDbContext db,
        TimeProvider timeProvider)
    {
        _db = db;
        _timeProvider = timeProvider;
    }

    public async Task<GameScreenshotOrderResult> ReorderAsync(
        long ownerUserId,
        long gameId,
        IReadOnlyList<Guid> mediaIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ownerUserId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gameId);
        ArgumentNullException.ThrowIfNull(mediaIds);

        var orderedIds = mediaIds.ToArray();
        if (orderedIds.Length > MediaPolicy.GameScreenshotMaximumCount
            || orderedIds.Any(id => id == Guid.Empty)
            || orderedIds.Distinct().Count() != orderedIds.Length)
        {
            return new GameScreenshotOrderResult(
                GameScreenshotOrderOutcome.InvalidOrder);
        }

        await using var transaction =
            await AccountMutationGuard.BeginTransactionIfNeededAsync(
                _db,
                cancellationToken);
        if (!await AccountMutationGuard.LockActiveAsync(
                _db,
                ownerUserId,
                cancellationToken))
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new GameScreenshotOrderResult(
                GameScreenshotOrderOutcome.AccountUnavailable);
        }

        var games = await _db.Games
            .FromSqlInterpolated(
                $"SELECT * FROM games WHERE id = {gameId} AND owner_id = {ownerUserId} FOR UPDATE")
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        if (games.Count != 1)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new GameScreenshotOrderResult(
                GameScreenshotOrderOutcome.GameNotFound);
        }

        var screenshots = await _db.MediaAssets
            .FromSqlInterpolated(
                $"""
                SELECT *
                FROM media_assets
                WHERE game_id = {gameId}
                  AND owner_user_id = {ownerUserId}
                  AND attachment_kind = 'GameScreenshot'
                  AND status = 'Ready'
                  AND is_current = TRUE
                FOR UPDATE
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        if (orderedIds.Length != screenshots.Count
            || !orderedIds.ToHashSet().SetEquals(
                screenshots.Select(item => item.Id)))
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new GameScreenshotOrderResult(
                GameScreenshotOrderOutcome.InvalidOrder);
        }

        var currentOrder = screenshots
            .OrderBy(item => item.SortOrder)
            .Select(item => item.Id)
            .ToArray();
        if (!currentOrder.SequenceEqual(orderedIds))
        {
            var publishedAtById = screenshots.ToDictionary(
                item => item.Id,
                item => item.PublishedAt!.Value);
            var publishedAt = orderedIds
                .Select(id => publishedAtById[id])
                .ToArray();
            var now = PostgreSqlTimestamp(_timeProvider.GetUtcNow());

            // The first update removes every row from the partial unique index;
            // the second restores the exact submitted set with contiguous slots.
            // Both statements are one parameterized database batch in this
            // transaction, so no intermediate lifecycle state is observable.
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE media_assets AS media
                SET status = 'ProcessingPending',
                    is_current = FALSE,
                    sort_order = NULL,
                    published_at = NULL,
                    updated_at = {now}
                WHERE media.game_id = {gameId}
                  AND media.owner_user_id = {ownerUserId}
                  AND media.attachment_kind = 'GameScreenshot'
                  AND media.status = 'Ready'
                  AND media.is_current = TRUE
                  AND media.id = ANY ({orderedIds});

                UPDATE media_assets AS media
                SET status = 'Ready',
                    is_current = TRUE,
                    sort_order = ordered.position - 1,
                    published_at = ordered.published_at,
                    updated_at = {now}
                FROM unnest({orderedIds}, {publishedAt})
                    WITH ORDINALITY AS ordered(id, published_at, position)
                WHERE media.id = ordered.id
                  AND media.game_id = {gameId}
                  AND media.owner_user_id = {ownerUserId}
                  AND media.attachment_kind = 'GameScreenshot'
                  AND media.status = 'ProcessingPending';
                """,
                cancellationToken);
        }

        await AccountMutationGuard.CommitIfOwnedAsync(
            transaction,
            cancellationToken);
        return new GameScreenshotOrderResult(
            GameScreenshotOrderOutcome.Reordered,
            orderedIds);
    }

    private static DateTimeOffset PostgreSqlTimestamp(DateTimeOffset value) =>
        value.AddTicks(-(value.Ticks % TimeSpan.TicksPerMicrosecond));
}
