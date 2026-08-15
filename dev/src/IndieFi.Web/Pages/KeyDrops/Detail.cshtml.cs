using IndieFi.Web.Application.Abstractions;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.KeyDistributions.Application;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Pages.KeyDrops;

public class DetailModel : PageModel
{
    private sealed record ClaimNotice(ClaimOutcome Outcome, string Code, string Message);

    private static readonly ClaimNotice[] ClaimNotices =
    [
        new(ClaimOutcome.NotOpen, "notopen", "아직 오픈되지 않았습니다."),
        new(ClaimOutcome.SoldOut, "soldout", "키가 모두 소진되었습니다."),
        new(ClaimOutcome.Conflict, "conflict", "동시 요청으로 키를 발급하지 못했습니다. 잠시 후 다시 시도해 주세요."),
    ];

    private readonly AppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly KeyClaimService _keyClaimService;

    public DetailModel(
        AppDbContext db,
        ICurrentUser currentUser,
        KeyClaimService keyClaimService)
    {
        _db = db;
        _currentUser = currentUser;
        _keyClaimService = keyClaimService;
    }

    public KeyDrop Drop { get; private set; } = null!;
    public Game Game { get; private set; } = null!;
    public bool IsOpen { get; private set; }
    public int RemainingKeys { get; private set; }
    public string? ClaimedKey { get; private set; }
    public string? ClaimMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(long id, string? claim)
    {
        // Query strings are user-controlled, so only known codes may select displayed text.
        ClaimMessage = ClaimNotices
            .FirstOrDefault(notice => notice.Code == claim)
            ?.Message;

        var drop = await _db.KeyDrops.FindAsync(id);

        if (drop is null)
        {
            return NotFound();
        }

        var game = await _db.Games.FindAsync(drop.GameId);

        if (game is null)
        {
            return NotFound();
        }

        Drop = drop;
        Game = game;

        // This is only for display. The real open boundary is enforced
        // using database time when a key is claimed.
        IsOpen = drop.OpensAt <= DateTimeOffset.Now;

        RemainingKeys = await _db.GameKeys
            .CountAsync(key =>
                key.DropId == id &&
                !_db.KeyGrants.Any(grant => grant.KeyId == key.Id));

        if (_currentUser.IsAuthenticated)
        {
            var userId = _currentUser.GetRequiredUserId();

            ClaimedKey = await _db.KeyGrants
                .Where(grant => grant.DropId == id && grant.UserId == userId)
                .Join(
                    _db.GameKeys,
                    grant => grant.KeyId,
                    key => key.Id,
                    (_, key) => key.KeyValue)
                .SingleOrDefaultAsync();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostClaimAsync(long id)
    {
        // The page itself stays public, so claiming is guarded here instead.
        // [Authorize] cannot do it: Razor Pages ignores the attribute on a handler.
        if (!_currentUser.IsAuthenticated)
        {
            return Challenge();
        }

        var userId = _currentUser.GetRequiredUserId();
        var result = await _keyClaimService.ClaimAsync(id, userId);

        if (result.Outcome == ClaimOutcome.DropNotFound)
        {
            return NotFound();
        }

        if (result.Outcome == ClaimOutcome.AccountUnavailable)
        {
            return Challenge();
        }

        var claimNotice = ClaimNotices
            .FirstOrDefault(notice => notice.Outcome == result.Outcome);

        if (claimNotice is not null)
        {
            return RedirectToPage(new { id, claim = claimNotice.Code });
        }

        return RedirectToPage(new { id });
    }
}
