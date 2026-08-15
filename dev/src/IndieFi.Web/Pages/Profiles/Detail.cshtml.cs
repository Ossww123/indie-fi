using IndieFi.Web.Application.Abstractions;
using IndieFi.Web.Modules.Profiles.Application;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IndieFi.Web.Pages.Profiles;

public sealed class DetailModel : PageModel
{
    private readonly ICurrentUser _currentUser;
    private readonly PublicUserProfileQueryService _profileService;

    public DetailModel(
        ICurrentUser currentUser,
        PublicUserProfileQueryService profileService)
    {
        _currentUser = currentUser;
        _profileService = profileService;
    }

    public PublicUserProfile Profile { get; private set; } = null!;
    public bool IsOwner { get; private set; }

    public async Task<IActionResult> OnGetAsync(long id)
    {
        var profile = await _profileService.GetAsync(
            id,
            HttpContext.RequestAborted);
        if (profile is null)
        {
            return NotFound();
        }

        Profile = profile;
        IsOwner = _currentUser.UserId == profile.UserId;
        return Page();
    }
}
