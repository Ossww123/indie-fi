using IndieFi.Web.Application.Abstractions;
using IndieFi.Web.Modules.Profiles.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IndieFi.Web.Pages.Account;

[Authorize]
public sealed class FavoriteGamesModel : PageModel
{
    private readonly ICurrentUser _currentUser;
    private readonly FavoriteGameService _favoriteGameService;

    public FavoriteGamesModel(
        ICurrentUser currentUser,
        FavoriteGameService favoriteGameService)
    {
        _currentUser = currentUser;
        _favoriteGameService = favoriteGameService;
    }

    public IReadOnlyList<FavoriteGameListItem> Items { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (_currentUser.UserId is not long userId)
        {
            return Challenge();
        }

        Items = await _favoriteGameService.GetFavoritesAsync(
            userId,
            HttpContext.RequestAborted);
        return Page();
    }
}
