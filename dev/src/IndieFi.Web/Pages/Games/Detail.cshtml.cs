using System.Security.Claims;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Pages.Games;

[Authorize]
public class DetailModel : PageModel
{
    private readonly AppDbContext _db;

    public DetailModel(AppDbContext db)
    {
        _db = db;
    }

    public Game Game { get; private set; } = null!;
    public bool IsOwner { get; private set; }
    public int ScheduledDropCount { get; private set; }

    public async Task<IActionResult> OnGetAsync(long id)
    {
        var game = await _db.Games.FindAsync(id);

        if (game is null)
        {
            return NotFound();
        }

        // The game detail page is public: any signed-in user may view any game,
        // because feedback added later is left by other people. Ownership is only
        // enforced when editing a game.
        Game = game;
        IsOwner = long.TryParse(
            User.FindFirstValue(ClaimTypes.NameIdentifier),
            out var ownerId) && game.OwnerId == ownerId;

        if (IsOwner)
        {
            ScheduledDropCount = await _db.KeyDrops.CountAsync(d => d.GameId == game.Id);
        }

        return Page();
    }
}
