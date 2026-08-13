using System.ComponentModel.DataAnnotations;
using IndieFi.Web.Application.Abstractions;
using IndieFi.Web.Data;
using IndieFi.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IndieFi.Web.Pages.Games;

[Authorize]
public class EditModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly GameManagementService _gameManagementService;

    public EditModel(
        AppDbContext db,
        ICurrentUser currentUser,
        GameManagementService gameManagementService)
    {
        _db = db;
        _currentUser = currentUser;
        _gameManagementService = gameManagementService;
    }

    [BindProperty]
    [Required(ErrorMessage = "게임 제목을 입력해 주세요.")]
    [StringLength(200, ErrorMessage = "제목이 너무 깁니다.")]
    public string Title { get; set; } = string.Empty;

    [BindProperty]
    [StringLength(2000, ErrorMessage = "소개가 너무 깁니다.")]
    public string? Description { get; set; }

    public async Task<IActionResult> OnGetAsync(long id)
    {
        var game = await _db.Games.FindAsync(id);
        var ownerId = _currentUser.GetRequiredUserId();

        // Return 404 (not 403) when the game is missing OR not owned by the caller —
        // a 403 would reveal that the game exists. Same reason the login page does
        // not distinguish "no such account" from "wrong password".
        if (game is null || game.OwnerId != ownerId)
        {
            return NotFound();
        }

        Title = game.Title;
        Description = game.Description;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(long id)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var ownerId = _currentUser.GetRequiredUserId();
        var result = await _gameManagementService.UpdateAsync(
            id,
            ownerId,
            Title,
            Description);

        if (result.Outcome == UpdateGameOutcome.NotFound)
        {
            return NotFound();
        }

        if (result.Outcome == UpdateGameOutcome.Invalid)
        {
            foreach (var violation in result.Violations)
            {
                ModelState.AddModelError(violation.Field, violation.Message);
            }

            return Page();
        }

        TempData["GameMessage"] = "수정되었습니다";
        return RedirectToPage("/Games/Detail", new { id });
    }
}
