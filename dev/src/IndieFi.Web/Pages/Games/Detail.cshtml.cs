using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

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
        return Page();
    }
}
