using IndieFi.Web.Application.Abstractions;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.KeyDistributions.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IndieFi.Web.Pages.KeyDrops;

[Authorize]
public class NewModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly KeyDropSchedulingService _keyDropSchedulingService;

    public NewModel(
        AppDbContext db,
        ICurrentUser currentUser,
        KeyDropSchedulingService keyDropSchedulingService)
    {
        _db = db;
        _currentUser = currentUser;
        _keyDropSchedulingService = keyDropSchedulingService;
    }

    public Game Game { get; private set; } = null!;

    // No [Required] here: the service decides whether the input is usable. Duplicating
    // the rule as an annotation would fail model binding first and return before the
    // service ever runs, hiding every other problem with the form.
    [BindProperty]
    public DateTimeOffset? OpensAt { get; set; }

    [BindProperty]
    public string? KeysText { get; set; }

    [BindProperty]
    public int? MinimumAccountAgeDays { get; set; }

    [BindProperty]
    public int? MinimumCompletedReviewCount { get; set; }

    public async Task<IActionResult> OnGetAsync(long gameId)
    {
        var ownerId = _currentUser.GetRequiredUserId();
        return await LoadPageAsync(gameId, ownerId);
    }

    public async Task<IActionResult> OnPostAsync(long gameId)
    {
        var ownerId = _currentUser.GetRequiredUserId();

        // Only binding failures are handled here — for example a date the browser could
        // not parse. Everything the service can judge is left to the service.
        if (!ModelState.IsValid)
        {
            return await LoadPageAsync(gameId, ownerId);
        }

        var result = await _keyDropSchedulingService.ScheduleAsync(
            gameId,
            ownerId,
            OpensAt,
            KeysText,
            MinimumAccountAgeDays,
            MinimumCompletedReviewCount);

        if (result.Outcome is
            ScheduleKeyDropOutcome.GameNotFound or
            ScheduleKeyDropOutcome.NotOwner)
        {
            return NotFound();
        }

        if (result.Outcome == ScheduleKeyDropOutcome.AccountUnavailable)
        {
            return Challenge();
        }

        foreach (var violation in result.Violations)
        {
            ModelState.AddModelError(violation.Field, violation.Message);
        }

        if (!ModelState.IsValid)
        {
            return await LoadPageAsync(gameId, ownerId);
        }

        TempData["GameMessage"] = $"드롭 예약 완료 (키 {result.KeyCount}개)";
        return RedirectToPage("/Games/Detail", new { id = gameId });
    }

    private async Task<IActionResult> LoadPageAsync(long gameId, long ownerId)
    {
        var game = await _db.Games.FindAsync(gameId);

        // Return 404 (not 403) when the game is missing OR not owned by the caller —
        // a 403 would reveal that the game exists.
        if (game is null || game.OwnerId != ownerId)
        {
            return NotFound();
        }

        Game = game;
        return Page();
    }
}
