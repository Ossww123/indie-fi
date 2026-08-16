using IndieFi.Web.Application.Abstractions;
using IndieFi.Web.Modules.Notifications.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IndieFi.Web.Pages.Notifications;

[Authorize]
public sealed class IndexModel : PageModel
{
    private readonly ICurrentUser _currentUser;
    private readonly NotificationInboxService _inbox;

    public IndexModel(
        ICurrentUser currentUser,
        NotificationInboxService inbox)
    {
        _currentUser = currentUser;
        _inbox = inbox;
    }

    public NotificationInboxSnapshot Snapshot { get; private set; } =
        new([], 0, null);
    public string? Notice { get; private set; }

    public async Task<IActionResult> OnGetAsync(
        DateTimeOffset? before,
        long? beforeId,
        string? status)
    {
        if (_currentUser.UserId is not long userId)
        {
            return Challenge();
        }

        NotificationCursor? cursor = before is not null && beforeId > 0
            ? new(before.Value, beforeId.Value)
            : null;
        Snapshot = await _inbox.GetAsync(
            userId,
            cursor: cursor,
            cancellationToken: HttpContext.RequestAborted);
        Notice = status switch
        {
            "read" => "알림을 읽음으로 표시했습니다.",
            "unread" => "알림을 읽지 않음으로 표시했습니다.",
            "all-read" => "모든 알림을 읽음으로 표시했습니다.",
            _ => null,
        };
        return Page();
    }

    public async Task<IActionResult> OnPostSetReadAsync(
        long id,
        bool isRead)
    {
        if (_currentUser.UserId is not long userId)
        {
            return Challenge();
        }

        var outcome = await _inbox.SetReadStateAsync(
            userId,
            id,
            isRead,
            HttpContext.RequestAborted);
        if (outcome == NotificationReadStateOutcome.NotFound)
        {
            return NotFound();
        }

        return RedirectToPage(new { status = isRead ? "read" : "unread" });
    }

    public async Task<IActionResult> OnPostReadAllAsync()
    {
        if (_currentUser.UserId is not long userId)
        {
            return Challenge();
        }

        await _inbox.MarkAllReadAsync(userId, HttpContext.RequestAborted);
        return RedirectToPage(new { status = "all-read" });
    }
}
