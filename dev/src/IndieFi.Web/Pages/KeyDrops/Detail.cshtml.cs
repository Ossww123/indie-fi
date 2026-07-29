using System.Security.Claims;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Pages.KeyDrops;

public class DetailModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly KeyClaimService _keyClaimService;

    public DetailModel(AppDbContext db, KeyClaimService keyClaimService)
    {
        _db = db;
        _keyClaimService = keyClaimService;
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

        var userId = long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await _keyClaimService.ClaimAsync(id, userId);

        if (result.Outcome == ClaimOutcome.DropNotFound)
        {
            return NotFound();
        }

        switch (result.Outcome)
        {
            case ClaimOutcome.NotOpen:
                TempData["ClaimMessage"] = "아직 오픈되지 않았습니다.";
                break;
            case ClaimOutcome.SoldOut:
                TempData["ClaimMessage"] = "키가 모두 소진되었습니다.";
                break;
            case ClaimOutcome.Conflict:
                TempData["ClaimMessage"] = "동시 요청으로 키를 발급하지 못했습니다. 잠시 후 다시 시도해 주세요.";
                break;
        }

        return RedirectToPage(new { id });
    }
}
