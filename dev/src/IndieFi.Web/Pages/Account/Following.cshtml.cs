using IndieFi.Web.Application.Abstractions;
using IndieFi.Web.Modules.Profiles.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IndieFi.Web.Pages.Account;

[Authorize]
public sealed class FollowingModel : PageModel
{
    private readonly ICurrentUser _currentUser;
    private readonly UserFollowService _followService;

    public FollowingModel(
        ICurrentUser currentUser,
        UserFollowService followService)
    {
        _currentUser = currentUser;
        _followService = followService;
    }

    public IReadOnlyList<FollowingListItem> Items { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (_currentUser.UserId is not long userId)
        {
            return Challenge();
        }

        Items = await _followService.GetFollowingAsync(
            userId,
            HttpContext.RequestAborted);
        return Page();
    }
}
