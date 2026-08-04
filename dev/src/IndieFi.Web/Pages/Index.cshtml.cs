using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Pages;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;

    public IndexModel(AppDbContext db)
    {
        _db = db;
    }

    public IReadOnlyList<Game> Games { get; private set; } = [];

    public async Task OnGetAsync()
    {
        // The home page is a public gallery: every registered game, newest first.
        // No owner filter and no [Authorize] — logged-out visitors browse here and
        // follow a game into its public detail (and its key drops).
        Games = await _db.Games
            .OrderByDescending(g => g.CreatedAt)
            .ToListAsync();
    }
}
