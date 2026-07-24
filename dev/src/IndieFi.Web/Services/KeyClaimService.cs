using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using Microsoft.EntityFrameworkCore;
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

    public KeyClaimService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<ClaimResult> ClaimAsync(long dropId, long userId)
    {
        var drop = await _db.KeyDrops.FindAsync(dropId);

        if (drop is null)
        {
            return new ClaimResult(ClaimOutcome.DropNotFound);
        }

        // One database clock keeps the open boundary consistent across app servers.
        var databaseNow = await _db.Database
            .SqlQuery<DateTimeOffset>($"SELECT now() AS \"Value\"")
            .SingleAsync();

        if (drop.OpensAt > databaseNow)
        {
            return new ClaimResult(ClaimOutcome.NotOpen);
        }

        var alreadyClaimed = await _db.KeyGrants
            .AnyAsync(grant => grant.DropId == dropId && grant.UserId == userId);

        if (alreadyClaimed)
        {
            return new ClaimResult(ClaimOutcome.AlreadyClaimed);
        }

        var key = await _db.SteamKeys
            .Where(key =>
                key.DropId == dropId &&
                !_db.KeyGrants.Any(grant => grant.KeyId == key.Id))
            .OrderBy(key => key.Id)
            .FirstOrDefaultAsync();

        if (key is null)
        {
            return new ClaimResult(ClaimOutcome.SoldOut);
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
            return new ClaimResult(ClaimOutcome.Granted);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            // The next step will observe callers racing for the same key, so do not retry here.
            return new ClaimResult(ClaimOutcome.Conflict);
        }
    }
}
