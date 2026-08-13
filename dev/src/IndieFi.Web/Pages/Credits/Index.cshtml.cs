using IndieFi.Web.Application.Abstractions;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Pages.Credits;

[Authorize]
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _currentUser;

    public IndexModel(AppDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public IReadOnlyList<CreditLedgerEntry> Entries { get; private set; } = [];

    public int Balance { get; private set; }

    public async Task OnGetAsync()
    {
        var userId = _currentUser.GetRequiredUserId();

        Entries = await _db.CreditLedgerEntries
            .Where(e => e.UserId == userId)
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync();

        // Show the same value protected by the CHECK constraint: summing the ledger
        // here could hide a divergence between it and the cached balance. This makes
        // two database round trips (ledger entries plus the user row) by design.
        Balance = await _db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.CreditBalance)
            .SingleAsync();
    }
}
