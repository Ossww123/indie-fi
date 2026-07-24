using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IndieFi.Web.Pages.KeyDrops;

public class DetailModel : PageModel
{
    private readonly AppDbContext _db;

    public DetailModel(AppDbContext db)
    {
        _db = db;
    }

    public KeyDrop Drop { get; private set; } = null!;
    public Game Game { get; private set; } = null!;
    public bool IsOpen { get; private set; }

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

        // This is only for display. The real open boundary will be enforced
        // using database time when a key is claimed in the next step.
        IsOpen = drop.OpensAt <= DateTimeOffset.Now;

        return Page();
    }
}
