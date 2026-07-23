using System.Security.Claims;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Pages.Games;

[Authorize]
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
        var ownerId = long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        // Only the signed-in user's own games; the owner id comes from the cookie,
        // same as when a game is registered.
        Games = await _db.Games
            .Where(g => g.OwnerId == ownerId)
            .OrderByDescending(g => g.CreatedAt)
            .ToListAsync();
    }
}
