using IndieFi.Web.Application.Abstractions;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Pages.Games;

// Public: anyone (including logged-out visitors) can view a game's detail and its
// key drops. IsOwner stays false for anonymous users, hiding the owner-only actions.
public class DetailModel : PageModel
{
    public sealed record RequestNotice(
        ReviewRequestOutcome Outcome,
        string Code,
        string Message,
        bool IsSuccess);

    private static readonly RequestNotice[] RequestNotices =
    [
        new(
            ReviewRequestOutcome.Queued,
            "queued",
            "피드백 요청을 등록했습니다. 크레딧 1을 사용했습니다.",
            true),
        new(
            ReviewRequestOutcome.InsufficientCredit,
            "nocredit",
            "크레딧이 부족합니다. 다른 개발자의 게임에 피드백을 주면 크레딧을 벌 수 있습니다.",
            false),
        new(
            ReviewRequestOutcome.Cancelled,
            "cancelled",
            "피드백 요청을 취소했습니다. 크레딧 1을 돌려받았습니다.",
            true),
        new(
            ReviewRequestOutcome.NotCancellable,
            "notcancellable",
            "이미 다른 개발자가 받아 갔거나 취소된 요청입니다.",
            false),
    ];

    private readonly AppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly ReviewRequestService _reviewRequestService;

    public DetailModel(
        AppDbContext db,
        ICurrentUser currentUser,
        ReviewRequestService reviewRequestService)
    {
        _db = db;
        _currentUser = currentUser;
        _reviewRequestService = reviewRequestService;
    }

    public Game Game { get; private set; } = null!;
    public bool IsOwner { get; private set; }
    public List<DropListItem> Drops { get; private set; } = [];
    public int PendingRequestCount { get; private set; }
    public List<RequestListItem> Requests { get; private set; } = [];
    public RequestNotice? RequestMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(long id, string? request)
    {
        // 028 showed that TempData notices can be lost, so the result travels in the URL.
        // Query strings are user-controlled, so only known codes may select displayed text.
        RequestMessage = RequestNotices
            .FirstOrDefault(notice => notice.Code == request);

        var game = await _db.Games.FindAsync(id);

        if (game is null)
        {
            return NotFound();
        }

        // The game detail page is public: any signed-in user may view any game,
        // because feedback added later is left by other people. Ownership is only
        // enforced when editing a game.
        Game = game;
        IsOwner = _currentUser.UserId is long ownerId && game.OwnerId == ownerId;

        if (IsOwner)
        {
            Requests = await _db.ReviewRequests
                .Where(reviewRequest =>
                    reviewRequest.GameId == game.Id
                    && reviewRequest.Status != ReviewRequestStatus.Cancelled)
                .OrderByDescending(reviewRequest => reviewRequest.Id)
                .Select(reviewRequest => new RequestListItem(
                    reviewRequest.Id,
                    reviewRequest.CreatedAt,
                    reviewRequest.Status))
                .ToListAsync();
        }
        else
        {
            PendingRequestCount = await _db.ReviewRequests
                .CountAsync(reviewRequest =>
                    reviewRequest.GameId == game.Id
                    && reviewRequest.Status == ReviewRequestStatus.Queued);
        }

        Drops = await _db.KeyDrops
            .Where(drop => drop.GameId == game.Id)
            .OrderBy(drop => drop.OpensAt)
            .Select(drop => new DropListItem(drop.Id, drop.OpensAt))
            .ToListAsync();

        return Page();
    }

    public async Task<IActionResult> OnPostRequestFeedbackAsync(long id)
    {
        // The page itself stays public, so requesting feedback is guarded here instead.
        // [Authorize] cannot do it: Razor Pages ignores the attribute on a handler.
        if (!_currentUser.IsAuthenticated)
        {
            return Challenge();
        }

        var userId = _currentUser.GetRequiredUserId();
        var result = await _reviewRequestService.RequestAsync(id, userId);

        if (result.Outcome == ReviewRequestOutcome.GameNotFound)
        {
            return NotFound();
        }

        if (result.Outcome == ReviewRequestOutcome.NotOwner)
        {
            return Forbid();
        }

        var requestNotice = RequestNotices
            .FirstOrDefault(notice => notice.Outcome == result.Outcome);

        if (requestNotice is not null)
        {
            return RedirectToPage(new { id, request = requestNotice.Code });
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostCancelRequestAsync(long id, long requestId)
    {
        // The page itself stays public, so cancellation is guarded here instead.
        // [Authorize] cannot do it: Razor Pages ignores the attribute on a handler.
        if (!_currentUser.IsAuthenticated)
        {
            return Challenge();
        }

        var result = await _reviewRequestService.CancelAsync(
            requestId,
            _currentUser.GetRequiredUserId());
        var requestNotice = RequestNotices
            .Single(notice => notice.Outcome == result.Outcome);

        return RedirectToPage(new { id, request = requestNotice.Code });
    }

    public sealed record RequestListItem(
        long Id,
        DateTimeOffset CreatedAt,
        ReviewRequestStatus Status)
    {
        public bool IsCancellable => Status == ReviewRequestStatus.Queued;

        public string StatusText => Status switch
        {
            ReviewRequestStatus.Queued => "대기 중",
            ReviewRequestStatus.Assigned => "배정됨",
            ReviewRequestStatus.Completed => "완료",
            _ => Status.ToString(),
        };
    }

    public sealed record DropListItem(long Id, DateTimeOffset OpensAt)
    {
        public bool IsOpen => OpensAt <= DateTimeOffset.Now;
    }
}
