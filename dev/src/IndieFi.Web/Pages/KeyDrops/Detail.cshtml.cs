using System.Security.Claims;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IndieFi.Web.Pages.KeyDrops;

public class DetailModel : PageModel
{
    /// <summary>PostgreSQL SQLSTATE for a unique constraint violation.</summary>
    private const string UniqueViolation = "23505";

    private readonly AppDbContext _db;

    public DetailModel(AppDbContext db)
    {
        _db = db;
    }

    public KeyDrop Drop { get; private set; } = null!;
    public Game Game { get; private set; } = null!;
    public bool IsOpen { get; private set; }
    public int RemainingKeys { get; private set; }
    public string? ClaimedKey { get; private set; }

    public async Task<IActionResult> OnGetAsync(long id)
    {
        var drop = await _db.KeyDrops.FindAsync(id);

        if (drop is null)
        {
            return NotFound();
        }

        var game = await _db.Games.FindAsync(drop.GameId);

        if (game is null)
        {
            return NotFound();
        }

        Drop = drop;
        Game = game;

        // This is only for display. The real open boundary is enforced
        // using database time when a key is claimed.
        IsOpen = drop.OpensAt <= DateTimeOffset.Now;

        RemainingKeys = await _db.SteamKeys
            .CountAsync(key =>
                key.DropId == id &&
                !_db.KeyGrants.Any(grant => grant.KeyId == key.Id));

        if (User.Identity?.IsAuthenticated == true)
        {
            var userId = long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

            ClaimedKey = await _db.KeyGrants
                .Where(grant => grant.DropId == id && grant.UserId == userId)
                .Join(
                    _db.SteamKeys,
                    grant => grant.KeyId,
                    key => key.Id,
                    (_, key) => key.KeyValue)
                .SingleOrDefaultAsync();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostClaimAsync(long id)
    {
        // The page itself stays public, so claiming is guarded here instead.
        // [Authorize] cannot do it: Razor Pages ignores the attribute on a handler.
        if (User.Identity?.IsAuthenticated != true)
        {
            return Challenge();
        }

        var drop = await _db.KeyDrops.FindAsync(id);

        if (drop is null)
        {
            return NotFound();
        }

        // One database clock keeps the open boundary consistent across app servers.
        var databaseNow = await _db.Database
            .SqlQuery<DateTimeOffset>($"SELECT now() AS \"Value\"")
            .SingleAsync();

        if (drop.OpensAt > databaseNow)
        {
            TempData["ClaimMessage"] = "아직 오픈되지 않았습니다.";
            return RedirectToPage(new { id });
        }

        var userId = long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var alreadyClaimed = await _db.KeyGrants
            .AnyAsync(grant => grant.DropId == id && grant.UserId == userId);

        if (alreadyClaimed)
        {
            return RedirectToPage(new { id });
        }

        var key = await _db.SteamKeys
            .Where(key =>
                key.DropId == id &&
                !_db.KeyGrants.Any(grant => grant.KeyId == key.Id))
            .OrderBy(key => key.Id)
            .FirstOrDefaultAsync();

        if (key is null)
        {
            TempData["ClaimMessage"] = "키가 모두 소진되었습니다.";
            return RedirectToPage(new { id });
        }

        _db.KeyGrants.Add(new KeyGrant
        {
            DropId = id,
            UserId = userId,
            KeyId = key.Id
        });

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            // The next step will observe callers racing for the same key, so do not retry here.
            TempData["ClaimMessage"] = "동시 요청으로 키를 발급하지 못했습니다. 잠시 후 다시 시도해 주세요.";
        }

        return RedirectToPage(new { id });
    }
}
