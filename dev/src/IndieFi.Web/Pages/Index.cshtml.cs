using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Infrastructure;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IndieFi.Web.Pages;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly MediaObjectStorageOptions _mediaStorageOptions;

    public IndexModel(
        AppDbContext db,
        IOptions<MediaObjectStorageOptions> mediaStorageOptions)
    {
        _db = db;
        _mediaStorageOptions = mediaStorageOptions.Value;
    }

    public IReadOnlyList<Game> Games { get; private set; } = [];
    public IReadOnlyDictionary<long, PublicGameMediaImage> Covers { get; private set; } =
        new Dictionary<long, PublicGameMediaImage>();

    public async Task OnGetAsync()
    {
        // The home page is a public gallery: every registered game, newest first.
        // No owner filter and no [Authorize] — logged-out visitors browse here and
        // follow a game into its public detail (and its key drops).
        Games = await _db.Games
            .OrderByDescending(g => g.CreatedAt)
            .ToListAsync();
        if (_mediaStorageOptions.Enabled && Games.Count > 0)
        {
            Covers = await HttpContext.RequestServices
                .GetRequiredService<GameMediaQueryService>()
                .GetPublicCoversAsync(
                    Games.Select(game => game.Id).ToArray(),
                    HttpContext.RequestAborted);
        }
    }
}
