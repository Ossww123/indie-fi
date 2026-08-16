using IndieFi.Web.Application.Abstractions;
using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IndieFi.Web.Pages.Account;

[Authorize]
public sealed class NotificationsModel : PageModel
{
    private readonly ICurrentUser _currentUser;
    private readonly NotificationPreferenceService _preferences;

    public NotificationsModel(
        ICurrentUser currentUser,
        NotificationPreferenceService preferences)
    {
        _currentUser = currentUser;
        _preferences = preferences;
    }

    [BindProperty]
    public List<NotificationPreferenceInput> Preferences { get; set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (_currentUser.UserId is not long userId)
        {
            return Challenge();
        }

        await LoadAsync(userId);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (_currentUser.UserId is not long userId)
        {
            return Challenge();
        }

        var selections = Preferences
            .Select(item => new NotificationPreferenceSelection(
                item.Kind,
                item.WebEnabled,
                item.EmailEnabled))
            .ToArray();
        var outcome = await _preferences.SaveAsync(
            userId,
            selections,
            HttpContext.RequestAborted);

        if (outcome == NotificationPreferenceSaveOutcome.Saved)
        {
            StatusMessage = "알림 설정을 저장했습니다.";
            return RedirectToPage();
        }

        ModelState.AddModelError(
            string.Empty,
            outcome == NotificationPreferenceSaveOutcome.AccountUnavailable
                ? "현재 계정에서는 알림 설정을 변경할 수 없습니다."
                : "알림 설정 항목이 올바르지 않습니다. 다시 확인해 주세요.");
        await LoadAsync(userId);
        return Page();
    }

    public static string GetLabel(NotificationKind kind) => kind switch
    {
        NotificationKind.ReviewAssigned => "리뷰 작업 배정",
        NotificationKind.ReviewDeadlineSoon => "리뷰 마감 임박",
        NotificationKind.ReviewExpired => "리뷰 작업 만료",
        NotificationKind.FeedbackCompleted => "내 게임의 피드백 완료",
        NotificationKind.KeyDropOpened => "신청한 키 배포 오픈",
        NotificationKind.FavoriteGameKeyDropCreated => "관심 게임의 새 키 배포",
        NotificationKind.FollowedUserGamePublished => "팔로우한 사용자의 새 공개 게임",
        NotificationKind.ReportResolved => "신고 처리 결과",
        NotificationKind.AccountStatusChanged => "계정 정지 및 해제",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private async Task LoadAsync(long userId)
    {
        Preferences = (await _preferences.GetAsync(
                userId,
                HttpContext.RequestAborted))
            .Select(item => new NotificationPreferenceInput
            {
                Kind = item.Kind,
                WebEnabled = item.WebEnabled,
                EmailEnabled = item.EmailEnabled,
                DefaultWebEnabled = item.DefaultWebEnabled,
                DefaultEmailEnabled = item.DefaultEmailEnabled,
                IsMandatory = item.IsMandatory,
                IsOverridden = item.IsOverridden,
            })
            .ToList();
    }

    public sealed class NotificationPreferenceInput
    {
        public NotificationKind Kind { get; set; }
        public bool WebEnabled { get; set; }
        public bool EmailEnabled { get; set; }
        public bool DefaultWebEnabled { get; set; }
        public bool DefaultEmailEnabled { get; set; }
        public bool IsMandatory { get; set; }
        public bool IsOverridden { get; set; }
    }
}
