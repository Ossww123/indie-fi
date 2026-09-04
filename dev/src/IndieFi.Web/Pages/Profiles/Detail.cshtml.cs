using IndieFi.Web.Application.Abstractions;
using IndieFi.Web.Modules.Feedback.Domain;
using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Infrastructure;
using IndieFi.Web.Modules.Profiles.Application;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace IndieFi.Web.Pages.Profiles;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class DetailModel : PageModel
{
    private readonly ICurrentUser _currentUser;
    private readonly PublicUserProfileQueryService _profileService;
    private readonly UserFollowService _followService;
    private readonly MediaObjectStorageOptions _mediaStorageOptions;

    public DetailModel(
        ICurrentUser currentUser,
        PublicUserProfileQueryService profileService,
        UserFollowService followService,
        IOptions<MediaObjectStorageOptions> mediaStorageOptions)
    {
        _currentUser = currentUser;
        _profileService = profileService;
        _followService = followService;
        _mediaStorageOptions = mediaStorageOptions.Value;
    }

    public PublicUserProfile Profile { get; private set; } = null!;
    public bool IsOwner { get; private set; }
    public bool CanManageFollow { get; private set; }
    public bool IsFollowing { get; private set; }
    public string? FollowMessage { get; private set; }
    public PublicProfileMediaImage? ProfileImage { get; private set; }
    public string ReviewerGradeLabel => Profile.ReviewerGrade switch
    {
        ReviewerGrade.Unranked => "등급 산정 전",
        ReviewerGrade.Bronze => "브론즈",
        ReviewerGrade.Silver => "실버",
        ReviewerGrade.Gold => "골드",
        ReviewerGrade.Platinum => "플래티넘",
        _ => throw new InvalidOperationException(
            $"Unsupported reviewer grade: {Profile.ReviewerGrade}"),
    };

    public async Task<IActionResult> OnGetAsync(long id, string? relationship)
    {
        if (_mediaStorageOptions.Enabled)
        {
            Response.Headers["Referrer-Policy"] = "no-referrer";
        }

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
        if (_mediaStorageOptions.Enabled)
        {
            var media = await HttpContext.RequestServices
                .GetRequiredService<ProfileMediaQueryService>()
                .GetPublicAsync(profile.UserId, HttpContext.RequestAborted);
            if (media is null)
            {
                return NotFound();
            }

            ProfileImage = media.Image;
        }

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
