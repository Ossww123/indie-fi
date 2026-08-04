using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Pages.Games;

// Public: anyone (including logged-out visitors) can view a game's detail and its
// key drops. IsOwner stays false for anonymous users, hiding the owner-only actions.
public class DetailModel : PageModel
{
    private readonly AppDbContext _db;

    public DetailModel(AppDbContext db)
    {
        _db = db;
    }

    public Game Game { get; private set; } = null!;
    public bool IsOwner { get; private set; }
    public List<DropListItem> Drops { get; private set; } = [];

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
        IsOwner = User.TryGetUserId(out var ownerId) && game.OwnerId == ownerId;

        Drops = await _db.KeyDrops
            .Where(drop => drop.GameId == game.Id)
            .OrderBy(drop => drop.OpensAt)
            .Select(drop => new DropListItem(drop.Id, drop.OpensAt))
            .ToListAsync();

        return Page();
    }

    public sealed record DropListItem(long Id, DateTimeOffset OpensAt)
    {
        public bool IsOpen => OpensAt <= DateTimeOffset.Now;
    }
}
