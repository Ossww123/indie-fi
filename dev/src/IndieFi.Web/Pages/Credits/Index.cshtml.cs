using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Pages.Credits;

[Authorize]
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;

    public IndexModel(AppDbContext db)
    {
        _db = db;
    }

    public IReadOnlyList<CreditLedgerEntry> Entries { get; private set; } = [];

    public int Balance { get; private set; }

    public async Task OnGetAsync()
    {
        var userId = User.GetUserId();

        Entries = await _db.CreditLedgerEntries
            .Where(e => e.UserId == userId)
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync();

        // All entries are already loaded for this unpaged screen, so summing them here
        // avoids a second database round trip. If the list is paginated, move the
        // balance calculation to a database SUM so it still covers the complete ledger.
        Balance = Entries.Sum(e => e.Delta);
    }
}
