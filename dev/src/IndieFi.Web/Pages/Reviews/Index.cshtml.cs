using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Extensions;
using IndieFi.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Pages.Reviews;

[Authorize]
public class IndexModel : PageModel
{
    public sealed record ClaimNotice(
        ReviewClaimOutcome Outcome,
        string Code,
        string Message,
        bool IsSuccess);

    private static readonly ClaimNotice[] ClaimNotices =
    [
        new(
            ReviewClaimOutcome.Claimed,
            "claimed",
            "리뷰 작업을 받았습니다. 마감시각까지 피드백을 제출해 주세요.",
            true),
        new(
            ReviewClaimOutcome.NoneAvailable,
            "none",
            "지금은 받을 수 있는 리뷰 작업이 없습니다.",
            false),
        new(
            ReviewClaimOutcome.AlreadyHasThatGame,
            "duplicate",
            "이미 받은 게임입니다. 받은 작업을 먼저 마쳐 주세요.",
            false),
    ];

    public sealed record SubmitNotice(
        FeedbackSubmissionOutcome Outcome,
        string Code,
        string Message,
        bool IsSuccess);

    private static readonly SubmitNotice[] SubmitNotices =
    [
        new(
            FeedbackSubmissionOutcome.Submitted,
            "submitted",
            "피드백을 제출했습니다.",
            true),
        new(
            FeedbackSubmissionOutcome.LeaseExpired,
            "expired",
            "마감시각이 지나 제출할 수 없습니다.",
            false),
        new(
            FeedbackSubmissionOutcome.NotSubmittable,
            "cannot",
            "제출할 수 없는 작업입니다.",
            false),
    ];

    public sealed record SweepNotice(string Message, bool IsSuccess);

    private const string SweptCode = "swept";

    private readonly AppDbContext _db;
    private readonly ReviewClaimService _reviewClaimService;
    private readonly ReviewLeaseSweepService _reviewLeaseSweepService;

    public IndexModel(
        AppDbContext db,
        ReviewClaimService reviewClaimService,
        ReviewLeaseSweepService reviewLeaseSweepService)
    {
        _db = db;
        _reviewClaimService = reviewClaimService;
        _reviewLeaseSweepService = reviewLeaseSweepService;
    }

    public IReadOnlyList<ReviewListItem> Items { get; private set; } = [];
    public IReadOnlyList<ReviewAssignmentListItem> MyAssignments { get; private set; } = [];
    public ClaimNotice? ClaimMessage { get; private set; }
    public SubmitNotice? SubmitMessage { get; private set; }
    public SweepNotice? SweepMessage { get; private set; }

    public async Task OnGetAsync(
        string? claim,
        string? submit,
        string? sweep,
        int? count)
    {
        // Query strings are user-controlled, so only known codes may select displayed text.
        ClaimMessage = ClaimNotices
            .FirstOrDefault(notice => notice.Code == claim);
        SubmitMessage = SubmitNotices
            .FirstOrDefault(notice => notice.Code == submit);
        // The swept count reaches the page as an int and is rendered as a number, so unlike
        // the codes above it cannot select or inject displayed text.
        if (sweep == SweptCode && count is int expiredCount and >= 0)
        {
            SweepMessage = expiredCount > 0
                ? new SweepNotice($"만료된 작업 {expiredCount}개를 큐로 되돌렸습니다.", true)
                : new SweepNotice("정리할 만료 작업이 없습니다.", false);
        }

        var userId = User.GetUserId();

        MyAssignments = await (
            from assignment in _db.ReviewAssignments
            join reviewRequest in _db.ReviewRequests on assignment.RequestId equals reviewRequest.Id
            join game in _db.Games on reviewRequest.GameId equals game.Id
            where assignment.ReviewerId == userId
            where assignment.Status == ReviewAssignmentStatus.Active
            orderby assignment.LeasedUntil
            select new ReviewAssignmentListItem(
                assignment.Id,
                game.Title,
                assignment.LeasedUntil))
            .ToListAsync();

        Items = await (
            from reviewRequest in _db.ReviewRequests
            join game in _db.Games on reviewRequest.GameId equals game.Id
            join requester in _db.Users on reviewRequest.RequesterId equals requester.Id
            where reviewRequest.Status == ReviewRequestStatus.Queued
            // A developer cannot review their own game, so their requests are hidden.
            where reviewRequest.RequesterId != userId
            // Ordering by id rather than created_at settles requests that share a
            // timestamp, and it survives a request returning to the queue later.
            orderby reviewRequest.Id
            select new ReviewListItem(
                game.Title,
                requester.Email,
                reviewRequest.CreatedAt))
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostClaimAsync()
    {
        var userId = User.GetUserId();
        var result = await _reviewClaimService.ClaimNextAsync(userId);
        var claimNotice = ClaimNotices
            .FirstOrDefault(notice => notice.Outcome == result.Outcome);

        if (claimNotice is not null)
        {
            return RedirectToPage(new { claim = claimNotice.Code });
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSweepAsync()
    {
        var result = await _reviewLeaseSweepService.SweepAsync();

        return RedirectToPage(new { sweep = SweptCode, count = result.ExpiredCount });
    }

    public sealed record ReviewListItem(
        string GameTitle,
        string RequesterEmail,
        DateTimeOffset RequestedAt);

    public sealed record ReviewAssignmentListItem(
        long AssignmentId,
        string GameTitle,
        DateTimeOffset LeasedUntil);
}
