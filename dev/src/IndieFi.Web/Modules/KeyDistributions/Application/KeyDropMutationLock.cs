using IndieFi.Web.Data;
using IndieFi.Web.Modules.KeyDistributions.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.KeyDistributions.Application;

public sealed record LockedKeyDropMutationState(
    bool AccountAvailable,
    long? DropId,
    long? OwnerId,
    DateTimeOffset? OpensAt,
    int? TotalKeys,
    int? RemainingKeys,
    DateTimeOffset? ClosedAt,
    long? StateVersion,
    DateTimeOffset DatabaseNow,
    bool AlreadyClaimed)
{
    public bool DropExists => DropId is not null;

    public KeyDropStatus GetRequiredStatus()
    {
        if (!DropExists
            || OpensAt is null
            || TotalKeys is null
            || RemainingKeys is null
            || StateVersion is null)
        {
            throw new InvalidOperationException(
                "A missing key drop does not have a mutation state.");
        }

        return KeyDropStatusPolicy.Resolve(
            OpensAt.Value,
            TotalKeys.Value,
            RemainingKeys.Value,
            ClosedAt,
            DatabaseNow);
    }
}

public sealed class KeyDropMutationLock
{
    private readonly AppDbContext _db;

    public KeyDropMutationLock(AppDbContext db)
    {
        _db = db;
    }

    public async Task<LockedKeyDropMutationState> LockAsync(
        long dropId,
        long actorId,
        CancellationToken cancellationToken = default)
    {
        if (_db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "A key-drop mutation lock requires an active transaction.");
        }

        // Every key-drop mutation locks the active actor first and the drop second.
        // The dependency between the materialized CTEs fixes that order across
        // claim, inventory, and close operations, including when the owner claims.
        var rows = await _db.Database
            .SqlQuery<LockRow>($$"""
                WITH active_user AS MATERIALIZED (
                    SELECT app_user.id
                    FROM users AS app_user
                    WHERE app_user.id = {{actorId}}
                      AND app_user.account_status = 'Active'
                      AND app_user.suspended_until IS NULL
                    FOR UPDATE
                ),
                locked_drop AS MATERIALIZED (
                    SELECT key_drop.id AS "DropId",
                           key_drop.owner_id AS "OwnerId",
                           key_drop.opens_at AS "OpensAt",
                           key_drop.total_keys AS "TotalKeys",
                           (
                               SELECT COUNT(*)::integer
                               FROM game_keys AS game_key
                               WHERE game_key.drop_id = key_drop.id
                                 AND NOT EXISTS (
                                     SELECT 1
                                     FROM key_grants AS grant_record
                                     WHERE grant_record.key_id = game_key.id
                                       AND grant_record.drop_id = key_drop.id)
                           ) AS "RemainingKeys",
                           key_drop.closed_at AS "ClosedAt",
                           key_drop.state_version AS "StateVersion",
                           EXISTS (
                               SELECT 1
                               FROM key_grants AS grant_record
                               WHERE grant_record.drop_id = key_drop.id
                                 AND grant_record.user_id = {{actorId}}
                           ) AS "AlreadyClaimed"
                    FROM key_drops AS key_drop
                    CROSS JOIN active_user
                    WHERE key_drop.id = {{dropId}}
                    FOR UPDATE OF key_drop
                )
                SELECT EXISTS (SELECT 1 FROM active_user) AS "AccountAvailable",
                       locked_drop."DropId",
                       locked_drop."OwnerId",
                       locked_drop."OpensAt",
                       locked_drop."TotalKeys",
                       locked_drop."RemainingKeys",
                       locked_drop."ClosedAt",
                       locked_drop."StateVersion",
                       now() AS "DatabaseNow",
                       COALESCE(locked_drop."AlreadyClaimed", false) AS "AlreadyClaimed"
                FROM (SELECT 1) AS anchor
                LEFT JOIN locked_drop ON true
                """)
            .ToListAsync(cancellationToken);
        var row = rows.Single();

        return new LockedKeyDropMutationState(
            row.AccountAvailable,
            row.DropId,
            row.OwnerId,
            row.OpensAt,
            row.TotalKeys,
            row.RemainingKeys,
            row.ClosedAt,
            row.StateVersion,
            row.DatabaseNow,
            row.AlreadyClaimed);
    }

    private sealed record LockRow
    {
        public bool AccountAvailable { get; init; }

        public long? DropId { get; init; }

        public long? OwnerId { get; init; }

        public DateTimeOffset? OpensAt { get; init; }

        public int? TotalKeys { get; init; }

        public int? RemainingKeys { get; init; }

        public DateTimeOffset? ClosedAt { get; init; }

        public long? StateVersion { get; init; }

        public DateTimeOffset DatabaseNow { get; init; }

        public bool AlreadyClaimed { get; init; }
    }
}
