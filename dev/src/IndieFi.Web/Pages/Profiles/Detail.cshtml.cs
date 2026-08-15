using IndieFi.Web.Application.Abstractions;
using IndieFi.Web.Modules.Profiles.Application;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IndieFi.Web.Pages.Profiles;

public sealed class DetailModel : PageModel
{
    private readonly ICurrentUser _currentUser;
    private readonly PublicUserProfileQueryService _profileService;
    private readonly UserFollowService _followService;

    public DetailModel(
        ICurrentUser currentUser,
        PublicUserProfileQueryService profileService,
        UserFollowService followService)
    {
        _currentUser = currentUser;
        _profileService = profileService;
        _followService = followService;
    }

    public PublicUserProfile Profile { get; private set; } = null!;
    public bool IsOwner { get; private set; }
    public bool CanManageFollow { get; private set; }
    public bool IsFollowing { get; private set; }
    public string? FollowMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(long id, string? relationship)
    {
        FollowMessage = relationship switch
        {
            "followed" => "팔로우했습니다.",
            "unfollowed" => "팔로우를 해제했습니다.",
            _ => null,
        };
        var profile = await _profileService.GetAsync(
            id,
            HttpContext.RequestAborted);
        if (profile is null)
        {
            return NotFound();
        }

        Profile = profile;
        IsOwner = _currentUser.UserId == profile.UserId;
        CanManageFollow = _currentUser.UserId is long && !IsOwner;
        if (CanManageFollow)
        {
            IsFollowing = await _followService.IsFollowingAsync(
                _currentUser.UserId!.Value,
                profile.UserId,
                HttpContext.RequestAborted);
        }

        return Page();
    }

    public Task<IActionResult> OnPostFollowAsync(long id) =>
        ChangeFollowAsync(id, follow: true);

    public Task<IActionResult> OnPostUnfollowAsync(long id) =>
        ChangeFollowAsync(id, follow: false);

    private async Task<IActionResult> ChangeFollowAsync(long id, bool follow)
    {
        if (_currentUser.UserId is not long userId)
        {
            return Challenge();
        }

        var result = follow
            ? await _followService.FollowAsync(
                userId,
                id,
                HttpContext.RequestAborted)
            : await _followService.UnfollowAsync(
                userId,
                id,
                HttpContext.RequestAborted);

        return result.Outcome switch
        {
            UserFollowOutcome.Following => RedirectToPage(
                new { id, relationship = "followed" }),
            UserFollowOutcome.NotFollowing => RedirectToPage(
                new { id, relationship = "unfollowed" }),
            UserFollowOutcome.CannotFollowSelf => BadRequest(),
            UserFollowOutcome.TargetNotFound => NotFound(),
            UserFollowOutcome.AccountUnavailable => Challenge(),
            _ => throw new InvalidOperationException(
                $"Unsupported follow outcome: {result.Outcome}"),
        };
    }
}
