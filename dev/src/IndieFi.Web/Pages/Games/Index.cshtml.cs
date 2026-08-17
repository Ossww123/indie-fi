using IndieFi.Web.Application.Abstractions;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IndieFi.Web.Pages.Games;

[Authorize]
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly MediaObjectStorageOptions _mediaStorageOptions;

    public IndexModel(
        AppDbContext db,
        ICurrentUser currentUser,
        IOptions<MediaObjectStorageOptions> mediaStorageOptions)
    {
        _db = db;
        _currentUser = currentUser;
        _mediaStorageOptions = mediaStorageOptions.Value;
    }

    public IReadOnlyList<Game> Games { get; private set; } = [];
    public IReadOnlyDictionary<long, PublicGameMediaImage> Covers { get; private set; } =
        new Dictionary<long, PublicGameMediaImage>();

    public async Task OnGetAsync()
    {
        var ownerId = _currentUser.GetRequiredUserId();

        // Only the signed-in user's own games; the owner id comes from the cookie,
        // same as when a game is registered.
        Games = await _db.Games
            .Where(g => g.OwnerId == ownerId)
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
