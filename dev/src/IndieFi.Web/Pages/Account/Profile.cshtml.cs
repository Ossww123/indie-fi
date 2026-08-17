using IndieFi.Web.Application.Abstractions;
using IndieFi.Web.Modules.Accounts.Domain;
using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Domain;
using IndieFi.Web.Modules.Media.Infrastructure;
using IndieFi.Web.Modules.Profiles.Application;
using IndieFi.Web.Modules.Profiles.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace IndieFi.Web.Pages.Account;

[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ProfileModel : PageModel
{
    private readonly ICurrentUser _currentUser;
    private readonly UserProfileManagementService _profileService;
    private readonly IServiceProvider _services;
    private readonly MediaObjectStorageOptions _storageOptions;

    public ProfileModel(
        ICurrentUser currentUser,
        UserProfileManagementService profileService,
        IServiceProvider services,
        IOptions<MediaObjectStorageOptions> storageOptions)
    {
        _currentUser = currentUser;
        _profileService = profileService;
        _services = services;
        _storageOptions = storageOptions.Value;
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
    public long PublicProfileUserId { get; private set; }
    public OwnerProfileMedia? Media { get; private set; }
    public long MaximumProfileImageBytes => MediaPolicy.ProfileImageMaximumBytes;

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (_storageOptions.Enabled)
        {
            Response.Headers["Referrer-Policy"] = "no-referrer";
        }

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
        if (!await LoadMediaAsync(userId))
        {
            return Challenge();
        }

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
        if (!await LoadMediaAsync(userId))
        {
            return Challenge();
        }

        return Page();
    }

    private void ApplyDisplayState(
        long userId,
        UserProfileEditState state,
        DateTimeOffset? resultAvailableAt = null)
    {
        FallbackDisplayIdentifier = UserDisplayIdentifier.Format(userId);
        PublicProfileUserId = userId;
        CanChangeNickname = state.CanChangeNickname;
        NicknameChangeAvailableAt = resultAvailableAt
            ?? state.NicknameChangeAvailableAt;
    }

    private async Task<bool> LoadMediaAsync(long userId)
    {
        if (!_storageOptions.Enabled)
        {
            Media = null;
            return true;
        }

        Media = await _services
            .GetRequiredService<ProfileMediaQueryService>()
            .GetOwnerAsync(userId, HttpContext.RequestAborted);
        return Media is not null;
    }

    public static string MediaStatusText(MediaStatus status) => status switch
    {
        MediaStatus.UploadPending => "업로드 대기",
        MediaStatus.ProcessingPending => "처리 대기",
        MediaStatus.Processing => "처리 중",
        MediaStatus.Ready => "공개 중",
        MediaStatus.Failed => "처리 실패",
        MediaStatus.Expired => "업로드 만료",
        _ => "사용할 수 없음",
    };

    public static string FormatBytes(long bytes) =>
        bytes >= 1024 * 1024
            ? $"{bytes / (1024d * 1024d):0.#} MiB"
            : $"{bytes / 1024d:0.#} KiB";

    public static bool CanRemoveMedia(MediaStatus status) =>
        MediaLifecyclePolicy.CanTransition(status, MediaStatus.DeletePending);
}
