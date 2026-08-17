using IndieFi.Web.Application.Abstractions;
using IndieFi.Web.Data;
using IndieFi.Web.Modules.Feedback.Application;
using IndieFi.Web.Modules.Feedback.Domain;
using IndieFi.Web.Modules.Profiles.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Pages.Games;

[Authorize]
public class FeedbackModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly FeedbackRatingService _feedbackRatings;
    private readonly UserDisplayNameResolver _displayNames;

    public FeedbackModel(
        AppDbContext db,
        ICurrentUser currentUser,
        FeedbackRatingService feedbackRatings,
        UserDisplayNameResolver displayNames)
    {
        _db = db;
        _currentUser = currentUser;
        _feedbackRatings = feedbackRatings;
        _displayNames = displayNames;
    }

    public long GameId { get; private set; }
    public string GameTitle { get; private set; } = string.Empty;
    public IReadOnlyList<FeedbackListItem> Items { get; private set; } = [];
    public long? SubmittedFeedbackId { get; private set; }
    public int? SubmittedScore { get; private set; }
    public string? SubmittedReason { get; private set; }
    public bool PreserveSubmittedRating { get; private set; }
    public IReadOnlyList<string> RatingErrors { get; private set; } = [];

    [TempData]
    public string? RatingMessage { get; set; }

    public Task<IActionResult> OnGetAsync(long id) =>
        LoadPageAsync(id, _currentUser.GetRequiredUserId());

    public async Task<IActionResult> OnPostRateAsync(
        long id,
        long feedbackId,
        int score,
        string? reason,
        int? revision)
    {
        var ownerId = _currentUser.GetRequiredUserId();
        if (!await IsOwnedFeedbackAsync(
                id,
                feedbackId,
                ownerId,
                HttpContext.RequestAborted))
        {
            return NotFound();
        }

        var result = await _feedbackRatings.SaveAsync(
            feedbackId,
            ownerId,
            score,
            reason,
            revision,
            HttpContext.RequestAborted);

        if (result.Outcome == FeedbackRatingSaveOutcome.NotFound)
        {
            return NotFound();
        }

        if (result.Outcome == FeedbackRatingSaveOutcome.AccountUnavailable)
        {
            return Challenge();
        }

        if (result.Outcome is
            FeedbackRatingSaveOutcome.Created or
            FeedbackRatingSaveOutcome.Updated or
            FeedbackRatingSaveOutcome.Unchanged)
        {
            RatingMessage = "평가를 저장했습니다.";
            return RedirectToPage(new { id, rating = "saved" });
        }

        SubmittedFeedbackId = feedbackId;
        if (result.Outcome == FeedbackRatingSaveOutcome.Invalid)
        {
            SubmittedScore = score;
            SubmittedReason = reason;
            PreserveSubmittedRating = true;
            RatingErrors = result.Violations
                .Select(violation => violation.Message)
                .ToList();
        }
        else if (result.Outcome == FeedbackRatingSaveOutcome.WindowClosed)
        {
            RatingErrors = ["평가 기간이 종료되어 저장할 수 없습니다."];
        }
        else if (result.Outcome == FeedbackRatingSaveOutcome.Conflict)
        {
            RatingErrors = ["다른 요청에서 평가가 변경되었습니다. 현재 내용을 확인한 뒤 다시 시도해 주세요."];
        }

        return await LoadPageAsync(id, ownerId);
    }

    public static string GetScoreLabel(int score) => score switch
    {
        1 => "매우 도움 안 됨",
        2 => "도움 안 됨",
        3 => "보통",
        4 => "도움 됨",
        5 => "매우 도움 됨",
        _ => string.Empty,
    };

    private async Task<IActionResult> LoadPageAsync(long id, long ownerId)
    {
        var game = await _db.Games
            .AsNoTracking()
            .Where(candidate => candidate.Id == id)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.OwnerId,
                candidate.Title,
            })
            .SingleOrDefaultAsync(HttpContext.RequestAborted);

        // Return 404 for both cases because 403 would confirm that the requested game exists.
        if (game is null || game.OwnerId != ownerId)
        {
            return NotFound();
        }

        GameId = game.Id;
        GameTitle = game.Title;
        var feedbackItems = await (
            from submission in _db.FeedbackSubmissions
            join assignment in _db.ReviewAssignments
                on submission.AssignmentId equals assignment.Id
            join reviewRequest in _db.ReviewRequests
                on assignment.RequestId equals reviewRequest.Id
            join existingRating in _db.FeedbackRatings
                on submission.Id equals existingRating.FeedbackSubmissionId into ratings
            from rating in ratings.DefaultIfEmpty()
            where reviewRequest.GameId == id
            // Ordering by id rather than created_at settles submissions that share a timestamp.
            orderby submission.Id descending
            select new FeedbackProjection(
                submission.Id,
                assignment.ReviewerId,
                submission.CreatedAt,
                submission.Body,
                rating == null ? null : rating.Score,
                rating == null ? null : rating.Reason,
                rating == null ? null : rating.Revision))
            .ToListAsync(HttpContext.RequestAborted);
        var databaseNow = await _db.Database
            .SqlQueryRaw<DateTimeOffset>("SELECT now() AS \"Value\"")
            .SingleAsync(HttpContext.RequestAborted);
        var displayNames = await _displayNames.ResolveAsync(
            feedbackItems.Select(item => item.ReviewerId),
            HttpContext.RequestAborted);

        Items = feedbackItems
            .Select(item =>
            {
                var ratingClosesAt = item.SubmittedAt.AddDays(
                    FeedbackQualityPolicy.RatingWindowDays);
                return new FeedbackListItem(
                    item.FeedbackId,
                    item.ReviewerId,
                    displayNames[item.ReviewerId],
                    item.SubmittedAt,
                    item.Body,
                    ratingClosesAt,
                    databaseNow <= ratingClosesAt,
                    item.RatingScore,
                    item.RatingReason,
                    item.RatingRevision);
            })
            .ToList();

        return Page();
    }

    private async Task<bool> IsOwnedFeedbackAsync(
        long gameId,
        long feedbackId,
        long ownerId,
        CancellationToken cancellationToken) =>
        await (
            from submission in _db.FeedbackSubmissions
            join assignment in _db.ReviewAssignments
                on submission.AssignmentId equals assignment.Id
            join reviewRequest in _db.ReviewRequests
                on assignment.RequestId equals reviewRequest.Id
            where submission.Id == feedbackId
                && reviewRequest.GameId == gameId
                && reviewRequest.RequesterId == ownerId
            select submission.Id)
            .AnyAsync(cancellationToken);

    public sealed record FeedbackListItem(
        long FeedbackId,
        long ReviewerId,
        string ReviewerDisplayName,
        DateTimeOffset SubmittedAt,
        string Body,
        DateTimeOffset RatingClosesAt,
        bool CanRate,
        int? RatingScore,
        string? RatingReason,
        int? RatingRevision);

    private sealed record FeedbackProjection(
        long FeedbackId,
        long ReviewerId,
        DateTimeOffset SubmittedAt,
        string Body,
        int? RatingScore,
        string? RatingReason,
        int? RatingRevision);
}
