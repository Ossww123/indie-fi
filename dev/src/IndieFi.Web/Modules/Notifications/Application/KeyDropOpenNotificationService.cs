using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IndieFi.Web.Modules.Notifications.Application;

public sealed record KeyDropOpenNotificationResult(int StagedCount);

public sealed class KeyDropOpenNotificationService
{
    public const int DefaultBatchSize = 100;

    private const string ClaimDueSql = """
        WITH due AS (
            SELECT id
              FROM key_drops
             WHERE opens_at <= now()
               AND closed_at IS NULL
               AND open_notification_staged_at IS NULL
             ORDER BY opens_at, id
             LIMIT @batchSize
             FOR UPDATE SKIP LOCKED
        )
        UPDATE key_drops AS key_drop
           SET open_notification_staged_at = now()
          FROM due
         WHERE key_drop.id = due.id
        RETURNING key_drop.id AS "DropId",
                  key_drop.owner_id AS "OwnerId",
                  key_drop.opens_at AS "OpensAt"
        """;

    private readonly AppDbContext _db;
    private readonly IKeyDropNotificationFanoutPublisher _fanoutPublisher;

    public KeyDropOpenNotificationService(
        AppDbContext db,
        IKeyDropNotificationFanoutPublisher fanoutPublisher)
    {
        _db = db;
        _fanoutPublisher = fanoutPublisher;
    }

    public async Task<KeyDropOpenNotificationResult> StageDueAsync(
        int batchSize = DefaultBatchSize,
        CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        await using var transaction =
            await AccountMutationGuard.BeginTransactionIfNeededAsync(
                _db,
                cancellationToken);
        var due = await _db.Database
            .SqlQueryRaw<DueKeyDrop>(
                ClaimDueSql,
                new NpgsqlParameter("batchSize", batchSize))
            .ToListAsync(cancellationToken);
        foreach (var drop in due)
        {
            await _fanoutPublisher.StageSubscribedDropOpenedAsync(
                NotificationEventId.Create(
                    NotificationKind.KeyDropOpened,
                    drop.DropId,
                    drop.OwnerId),
                drop.DropId,
                drop.OpensAt,
                cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
        await AccountMutationGuard.CommitIfOwnedAsync(transaction, cancellationToken);
        return new KeyDropOpenNotificationResult(due.Count);
    }

    private sealed record DueKeyDrop(
        long DropId,
        long OwnerId,
        DateTimeOffset OpensAt);
}
