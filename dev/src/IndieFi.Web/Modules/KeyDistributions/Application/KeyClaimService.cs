using IndieFi.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace IndieFi.Web.Modules.KeyDistributions.Application;

public enum ClaimOutcome
{
    Granted,
    AlreadyClaimed,
    NotOpen,
    SoldOut,
    Conflict,
    DropNotFound,
    AccountUnavailable,
}

public record ClaimResult(ClaimOutcome Outcome);

public class KeyClaimService
{
    private const string UniqueViolation = "23505";
    private const string OneKeyPerUserIndex = "IX_key_grants_drop_id_user_id";

    private sealed record ClaimPrecheck
    {
        public bool IsOpen { get; init; }
        public bool AlreadyClaimed { get; init; }
    }

    private readonly AppDbContext _db;
    private readonly ILogger<KeyClaimService> _logger;

    public KeyClaimService(AppDbContext db, ILogger<KeyClaimService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<ClaimResult> ClaimAsync(long dropId, long userId)
    {
        // Evaluate the open boundary inside the database so every app server uses
        // the same clock. This also combines all independent pre-checks in one trip.
        var precheck = await _db.Database
            .SqlQuery<ClaimPrecheck>($@"
                SELECT (d.opens_at <= now()) AS ""IsOpen"",
                       EXISTS (
                           SELECT 1
                           FROM key_grants AS g
                           WHERE g.drop_id = d.id AND g.user_id = {userId}
                       ) AS ""AlreadyClaimed""
                FROM key_drops AS d
                WHERE d.id = {dropId}")
            .SingleOrDefaultAsync();

        if (precheck is null)
        {
            return Result(ClaimOutcome.DropNotFound, dropId, userId);
        }

        if (!precheck.IsOpen)
        {
            return Result(ClaimOutcome.NotOpen, dropId, userId);
        }

        if (precheck.AlreadyClaimed)
        {
            return Result(ClaimOutcome.AlreadyClaimed, dropId, userId);
        }

        // One PostgreSQL statement is one transaction, so the FOR UPDATE lock survives
        // through the INSERT and no explicit transaction is needed. A residual race remains:
        // the lock covers game_keys, while "taken" lives in key_grants; under READ COMMITTED,
        // another claimer can commit between our snapshot and lock acquisition. The unique
        // index rejects that collision, so retries are still required.
        const int maxAttempts = 5;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var claimMarkers = await _db.Database
                    .SqlQuery<long>($@"
                        WITH active_user AS MATERIALIZED (
                            SELECT id
                            FROM users
                            WHERE id = {userId}
                              AND account_status = 'Active'
                              AND suspended_until IS NULL
                            FOR UPDATE
                        ),
                        picked AS (
                            SELECT s.id
                            FROM game_keys AS s
                            CROSS JOIN active_user
                            WHERE s.drop_id = {dropId}
                              AND NOT EXISTS (
                                  SELECT 1 FROM key_grants AS g WHERE g.key_id = s.id)
                            ORDER BY s.id
                            FOR UPDATE SKIP LOCKED
                            LIMIT 1
                        ),
                        granted AS (
                            INSERT INTO key_grants (drop_id, user_id, key_id)
                            SELECT {dropId}, {userId}, picked.id FROM picked
                            RETURNING key_id
                        )
                        SELECT COALESCE(
                            (SELECT key_id FROM granted),
                            CASE
                                WHEN EXISTS (SELECT 1 FROM active_user) THEN -1
                                ELSE -2
                            END) AS ""Value""")
                    .ToListAsync();

                var claimMarker = claimMarkers.Single();
                if (claimMarker == -2)
                {
                    return Result(ClaimOutcome.AccountUnavailable, dropId, userId);
                }

                if (claimMarker == -1)
                {
                    // No key is free for us: every key is granted or held by someone else.
                    return Result(ClaimOutcome.SoldOut, dropId, userId);
                }

                return Result(ClaimOutcome.Granted, dropId, userId);
            }
            catch (PostgresException exception) when (exception.SqlState == UniqueViolation)
            {
                // The two unique indexes mean opposites: a key collision can be retried,
                // while a one-key-per-user violation can never succeed on retry.
                // This name comes from EF Core's convention; if a migration renames it,
                // this branch silently stops matching, so the concurrency test guards it.
                if (exception.ConstraintName == OneKeyPerUserIndex)
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
