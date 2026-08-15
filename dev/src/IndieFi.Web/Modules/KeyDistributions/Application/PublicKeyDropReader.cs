using IndieFi.Web.Data;
using IndieFi.Web.Modules.KeyDistributions.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.KeyDistributions.Application;

public sealed record PublicKeyDropDetail(
    long Id,
    long GameId,
    string GameTitle,
    DateTimeOffset OpensAt,
    int TotalKeys,
    int RemainingKeys,
    KeyDropStatus Status)
{
    public string StatusText => Status switch
    {
        KeyDropStatus.Scheduled => "오픈 예정",
        KeyDropStatus.Active => "진행 중",
        KeyDropStatus.SoldOut => "소진",
        KeyDropStatus.ClosedEarly => "조기 마감",
        _ => throw new InvalidOperationException(
            $"Unsupported key drop status: {Status}"),
    };

    public bool CanClaim => KeyDropStatusPolicy.CanClaim(Status);

    public bool CanAddInventory => KeyDropStatusPolicy.CanAddInventory(Status);

    public bool CanCloseEarly => KeyDropStatusPolicy.CanCloseEarly(Status);
}

public sealed class PublicKeyDropReader
{
    private readonly AppDbContext _db;

    public PublicKeyDropReader(AppDbContext db)
    {
        _db = db;
    }

    public async Task<PublicKeyDropDetail?> GetAsync(
        long dropId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dropId);

        var row = await _db.Database
            .SqlQuery<PublicKeyDropRow>($$"""
                SELECT key_drop.id AS "Id",
                       key_drop.game_id AS "GameId",
                       game.title AS "GameTitle",
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
                       now() AS "DatabaseNow"
                FROM key_drops AS key_drop
                INNER JOIN games AS game ON game.id = key_drop.game_id
                WHERE key_drop.id = {{dropId}}
                """)
            .SingleOrDefaultAsync(cancellationToken);

        return row?.ToPublicDetail();
    }

    public async Task<IReadOnlyList<PublicKeyDropDetail>> ListForGameAsync(
        long gameId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gameId);

        var rows = await _db.Database
            .SqlQuery<PublicKeyDropRow>($$"""
                SELECT key_drop.id AS "Id",
                       key_drop.game_id AS "GameId",
                       game.title AS "GameTitle",
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
                       now() AS "DatabaseNow"
                FROM key_drops AS key_drop
                INNER JOIN games AS game ON game.id = key_drop.game_id
                WHERE key_drop.game_id = {{gameId}}
                ORDER BY key_drop.opens_at, key_drop.id
                """)
            .ToListAsync(cancellationToken);

        return rows.Select(row => row.ToPublicDetail()).ToArray();
    }

    private sealed record PublicKeyDropRow
    {
        public long Id { get; init; }

        public long GameId { get; init; }

        public string GameTitle { get; init; } = string.Empty;

        public DateTimeOffset OpensAt { get; init; }

        public int TotalKeys { get; init; }

        public int RemainingKeys { get; init; }

        public DateTimeOffset? ClosedAt { get; init; }

        public DateTimeOffset DatabaseNow { get; init; }

        public PublicKeyDropDetail ToPublicDetail()
        {
            var status = KeyDropStatusPolicy.Resolve(
                OpensAt,
                TotalKeys,
                RemainingKeys,
                ClosedAt,
                DatabaseNow);
            return new PublicKeyDropDetail(
                Id,
                GameId,
                GameTitle,
                OpensAt,
                TotalKeys,
                RemainingKeys,
                status);
        }
    }
}
