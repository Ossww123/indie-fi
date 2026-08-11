using IndieFi.Web.Data;
using IndieFi.Web.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Pages.Reviews;

[Authorize]
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;

    public IndexModel(AppDbContext db)
    {
        _db = db;
    }

    public IReadOnlyList<ReviewListItem> Items { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var userId = User.GetUserId();

        Items = await (
            from reviewRequest in _db.ReviewRequests
            join game in _db.Games on reviewRequest.GameId equals game.Id
            join requester in _db.Users on reviewRequest.RequesterId equals requester.Id
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

    public sealed record ReviewListItem(
        string GameTitle,
        string RequesterEmail,
        DateTimeOffset RequestedAt);
}
