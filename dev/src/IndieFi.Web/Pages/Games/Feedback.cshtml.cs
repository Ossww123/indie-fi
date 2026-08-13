using IndieFi.Web.Application.Abstractions;
using IndieFi.Web.Data;
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

    public FeedbackModel(AppDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public long GameId { get; private set; }
    public string GameTitle { get; private set; } = string.Empty;
    public IReadOnlyList<FeedbackListItem> Items { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(long id)
    {
        var game = await _db.Games.FindAsync(id);
        var ownerId = _currentUser.GetRequiredUserId();

        // Return 404 for both cases because 403 would confirm that the requested game exists.
        if (game is null || game.OwnerId != ownerId)
        {
            return NotFound();
        }

        GameId = game.Id;
        GameTitle = game.Title;
        Items = await (
            from submission in _db.FeedbackSubmissions
            join assignment in _db.ReviewAssignments on submission.AssignmentId equals assignment.Id
            join reviewRequest in _db.ReviewRequests on assignment.RequestId equals reviewRequest.Id
            join reviewer in _db.Users on assignment.ReviewerId equals reviewer.Id
            where reviewRequest.GameId == id
            // Ordering by id rather than created_at settles submissions that share a timestamp.
            orderby submission.Id descending
            select new FeedbackListItem(
                reviewer.Email,
                submission.CreatedAt,
                submission.Body))
            .ToListAsync();

        return Page();
    }

    public sealed record FeedbackListItem(
        string ReviewerEmail,
        DateTimeOffset SubmittedAt,
        string Body);
}
