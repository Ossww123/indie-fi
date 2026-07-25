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

        var key = await _db.SteamKeys
            .Where(key =>
                key.DropId == dropId &&
                !_db.KeyGrants.Any(grant => grant.KeyId == key.Id))
            .OrderBy(key => key.Id)
            .FirstOrDefaultAsync();

        if (key is null)
        {
            return Result(ClaimOutcome.SoldOut, dropId, userId);
        }

        _db.KeyGrants.Add(new KeyGrant
        {
            DropId = dropId,
            UserId = userId,
            KeyId = key.Id
        });

        try
        {
            await _db.SaveChangesAsync();
            return Result(ClaimOutcome.Granted, dropId, userId);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            // The next step will observe callers racing for the same key, so do not retry here.
            return Result(ClaimOutcome.Conflict, dropId, userId);
        }
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
