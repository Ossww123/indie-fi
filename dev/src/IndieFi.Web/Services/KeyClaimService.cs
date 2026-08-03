using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace IndieFi.Web.Services;

public enum ClaimOutcome
{
    Granted,
    AlreadyClaimed,
    NotOpen,
    SoldOut,
    Conflict,
    DropNotFound,
}

public record ClaimResult(ClaimOutcome Outcome);

public class KeyClaimService
{
    private const string UniqueViolation = "23505";
    private const string OneKeyPerUserIndex = "IX_key_grants_drop_id_user_id";

    private readonly AppDbContext _db;
    private readonly ILogger<KeyClaimService> _logger;

    public KeyClaimService(AppDbContext db, ILogger<KeyClaimService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<ClaimResult> ClaimAsync(long dropId, long userId)
    {
        var drop = await _db.KeyDrops.FindAsync(dropId);

        if (drop is null)
        {
            return Result(ClaimOutcome.DropNotFound, dropId, userId);
        }

        // One database clock keeps the open boundary consistent across app servers.
        var databaseNow = await _db.Database
            .SqlQuery<DateTimeOffset>($"SELECT now() AS \"Value\"")
            .SingleAsync();

        if (drop.OpensAt > databaseNow)
        {
            return Result(ClaimOutcome.NotOpen, dropId, userId);
        }

        var alreadyClaimed = await _db.KeyGrants
            .AnyAsync(grant => grant.DropId == dropId && grant.UserId == userId);

        if (alreadyClaimed)
        {
            return Result(ClaimOutcome.AlreadyClaimed, dropId, userId);
        }

        // Hand this claimer one free key. FOR UPDATE SKIP LOCKED lets concurrent
        // claimers each lock a DIFFERENT free key instead of all colliding on the lowest
        // id, so nobody fails while keys remain. EF Core cannot express SKIP LOCKED, so
        // this is raw SQL (the concurrency-critical path).
        //
        // One residual race remains: we lock the steam_keys row, but "taken" lives in
        // key_grants. Under READ COMMITTED another claimer can commit a grant for the
        // same key between our snapshot and our lock, so the unique index still rejects
        // us. That is rare, so we simply retry onto the next free key a few times.
        const int maxAttempts = 5;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            await using var transaction = await _db.Database.BeginTransactionAsync();

            var keyIds = await _db.Database
                .SqlQuery<long>($@"
                    SELECT s.id AS ""Value""
                    FROM steam_keys AS s
                    WHERE s.drop_id = {dropId}
                      AND NOT EXISTS (
                          SELECT 1 FROM key_grants AS g WHERE g.key_id = s.id)
                    ORDER BY s.id
                    FOR UPDATE SKIP LOCKED
                    LIMIT 1")
                .ToListAsync();

            if (keyIds.Count == 0)
            {
                // No key is free for us: every key is granted or held by someone else.
                return Result(ClaimOutcome.SoldOut, dropId, userId);
            }

            var grant = new KeyGrant
            {
                DropId = dropId,
                UserId = userId,
                KeyId = keyIds[0]
            };
            _db.KeyGrants.Add(grant);

            try
            {
                await _db.SaveChangesAsync();
                await transaction.CommitAsync();
                return Result(ClaimOutcome.Granted, dropId, userId);
            }
            catch (DbUpdateException exception)
                when (exception.InnerException is PostgresException { SqlState: UniqueViolation } postgresException)
            {
                // The two unique indexes mean opposites: a key collision can be retried,
                // while a one-key-per-user violation can never succeed on retry.
                // This name comes from EF Core's convention; if a migration renames it,
                // this branch silently stops matching, so the concurrency test guards it.
                _db.Entry(grant).State = EntityState.Detached;

                if (postgresException.ConstraintName == OneKeyPerUserIndex)
                {
                    return Result(ClaimOutcome.AlreadyClaimed, dropId, userId);
                }
            }
        }

        // Exhausted retries - lost the race every time. Practically unreachable.
        return Result(ClaimOutcome.Conflict, dropId, userId);
    }

    // One structured line per claim. Under load the screen message (redirect +
    // one-shot TempData cookie) is an unreliable signal, so the outcome - especially
    // Conflict vs SoldOut - is counted from these logs instead.
    private ClaimResult Result(ClaimOutcome outcome, long dropId, long userId)
    {
        _logger.LogInformation(
            "KeyClaim outcome={Outcome} drop={DropId} user={UserId}",
            outcome, dropId, userId);
        return new ClaimResult(outcome);
    }
}
