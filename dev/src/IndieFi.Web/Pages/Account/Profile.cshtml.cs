using IndieFi.Web.Application.Abstractions;
using IndieFi.Web.Modules.Accounts.Domain;
using IndieFi.Web.Modules.Profiles.Application;
using IndieFi.Web.Modules.Profiles.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IndieFi.Web.Pages.Account;

[Authorize]
public sealed class ProfileModel : PageModel
{
    private readonly ICurrentUser _currentUser;
    private readonly UserProfileManagementService _profileService;

    public ProfileModel(
        ICurrentUser currentUser,
        UserProfileManagementService profileService)
    {
        _currentUser = currentUser;
        _profileService = profileService;
    }

    [BindProperty]
    public string? Nickname { get; set; }

    [BindProperty]
    public string? Biography { get; set; }

    public int NicknameMinLength => UserProfilePolicy.NicknameMinLength;
    public int NicknameMaxLength => UserProfilePolicy.NicknameMaxLength;
    public int BiographyMaxLength => UserProfilePolicy.BiographyMaxLength;
    public bool CanChangeNickname { get; private set; }
    public DateTimeOffset? NicknameChangeAvailableAt { get; private set; }
    public string FallbackDisplayIdentifier { get; private set; } = string.Empty;

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (_currentUser.UserId is not long userId)
        {
            return Challenge();
        }

        var state = await _profileService.GetForEditAsync(
            userId,
            HttpContext.RequestAborted);
        if (state is null)
        {
            return Challenge();
        }

        Nickname = state.Nickname;
        Biography = state.Biography;
        ApplyDisplayState(userId, state);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (_currentUser.UserId is not long userId)
        {
            return Challenge();
        }

        var result = await _profileService.UpdateAsync(
            userId,
            Nickname,
            Biography,
            HttpContext.RequestAborted);

        if (result.Outcome == UserProfileUpdateOutcome.Updated)
        {
            StatusMessage = "프로필이 저장되었습니다.";
            return RedirectToPage();
        }

        if (result.Outcome == UserProfileUpdateOutcome.AccountUnavailable)
        {
            ModelState.AddModelError(
                string.Empty,
                "이 계정에서는 프로필을 변경할 수 없습니다.");
        }
        else
        {
            foreach (var violation in result.Violations)
            {
                ModelState.AddModelError(violation.Field, violation.Message);
            }
        }

        var state = await _profileService.GetForEditAsync(
            userId,
            HttpContext.RequestAborted);
        if (state is null)
        {
            return Challenge();
        }

        if (result.Outcome == UserProfileUpdateOutcome.NicknameChangeTooSoon)
        {
            ModelState.SetModelValue(
                nameof(Nickname),
                state.Nickname,
                state.Nickname);
        }

        ApplyDisplayState(userId, state, result.NicknameChangeAvailableAt);
        return Page();
    }

    private void ApplyDisplayState(
        long userId,
        UserProfileEditState state,
        DateTimeOffset? resultAvailableAt = null)
    {
        FallbackDisplayIdentifier = UserDisplayIdentifier.Format(userId);
        CanChangeNickname = state.CanChangeNickname;
        NicknameChangeAvailableAt = resultAvailableAt
            ?? state.NicknameChangeAvailableAt;
    }
}
