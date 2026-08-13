using System.ComponentModel.DataAnnotations;
using IndieFi.Web.Application.Abstractions;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Services;
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

    [BindProperty]
    [Required(ErrorMessage = "오픈 시각을 입력해 주세요.")]
    public DateTimeOffset? OpensAt { get; set; }

    [BindProperty]
    [Required(ErrorMessage = "키를 한 개 이상 입력해 주세요.")]
    public string KeysText { get; set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(long gameId)
    {
        var ownerId = _currentUser.GetRequiredUserId();
        return await LoadPageAsync(gameId, ownerId);
    }

    public async Task<IActionResult> OnPostAsync(long gameId)
    {
        var ownerId = _currentUser.GetRequiredUserId();

        if (!ModelState.IsValid)
        {
            return await LoadPageAsync(gameId, ownerId);
        }

        var result = await _keyDropSchedulingService.ScheduleAsync(
            gameId,
            ownerId,
            OpensAt!.Value,
            KeysText);

        if (result.Outcome is
            ScheduleKeyDropOutcome.GameNotFound or
            ScheduleKeyDropOutcome.NotOwner)
        {
            return NotFound();
        }

        if (result.Outcome == ScheduleKeyDropOutcome.NoKeys)
        {
            ModelState.AddModelError(nameof(KeysText), "키를 한 개 이상 입력해 주세요.");
        }

        if (result.Outcome == ScheduleKeyDropOutcome.OpenTimeNotFuture)
        {
            ModelState.AddModelError(nameof(OpensAt), "오픈 시각은 현재보다 미래여야 합니다.");
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
