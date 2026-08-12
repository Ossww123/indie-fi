using System.ComponentModel.DataAnnotations;
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
public class SubmitModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly FeedbackSubmissionService _feedbackSubmissionService;

    public SubmitModel(
        AppDbContext db,
        FeedbackSubmissionService feedbackSubmissionService)
    {
        _db = db;
        _feedbackSubmissionService = feedbackSubmissionService;
    }

    public string GameTitle { get; private set; } = string.Empty;
    public DateTimeOffset LeasedUntil { get; private set; }

    [BindProperty]
    [Required(ErrorMessage = "피드백을 입력해 주세요.")]
    [StringLength(4000, MinimumLength = 30, ErrorMessage = "피드백은 30자 이상 써 주세요.")]
    public string Body { get; set; } = string.Empty;

    public Task<IActionResult> OnGetAsync(long assignmentId)
    {
        return LoadPageAsync(assignmentId);
    }

    public async Task<IActionResult> OnPostAsync(long assignmentId)
    {
        if (!ModelState.IsValid)
        {
            return await LoadPageAsync(assignmentId);
        }

        var reviewerId = User.GetUserId();
        var result = await _feedbackSubmissionService.SubmitAsync(
            assignmentId,
            reviewerId,
            Body);
        var code = result.Outcome switch
        {
            FeedbackSubmissionOutcome.Submitted => "submitted",
            FeedbackSubmissionOutcome.LeaseExpired => "expired",
            FeedbackSubmissionOutcome.NotSubmittable => "cannot",
            _ => null,
        };

        return code is null
            ? RedirectToPage("Index")
            : RedirectToPage("Index", new { submit = code });
    }

    private async Task<IActionResult> LoadPageAsync(long assignmentId)
    {
        var reviewerId = User.GetUserId();
        var assignment = await (
            from reviewAssignment in _db.ReviewAssignments
            join reviewRequest in _db.ReviewRequests
                on reviewAssignment.RequestId equals reviewRequest.Id
            join game in _db.Games on reviewRequest.GameId equals game.Id
            where reviewAssignment.Id == assignmentId
            where reviewAssignment.ReviewerId == reviewerId
            where reviewAssignment.Status == ReviewAssignmentStatus.Active
            select new AssignmentDetails(
                game.Title,
                reviewAssignment.LeasedUntil))
            .SingleOrDefaultAsync();

        // Missing and other reviewers' assignments share one response so route probing cannot
        // reveal whether an assignment exists.
        if (assignment is null)
        {
            return RedirectToPage("Index", new { submit = "cannot" });
        }

        if (assignment.LeasedUntil <= DateTimeOffset.UtcNow)
        {
            return RedirectToPage("Index", new { submit = "expired" });
        }

        GameTitle = assignment.GameTitle;
        LeasedUntil = assignment.LeasedUntil;
        return Page();
    }

    private sealed record AssignmentDetails(
        string GameTitle,
        DateTimeOffset LeasedUntil);
}
