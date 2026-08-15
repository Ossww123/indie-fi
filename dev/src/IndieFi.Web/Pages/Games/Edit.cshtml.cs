using IndieFi.Web.Application.Abstractions;
using IndieFi.Web.Data;
using IndieFi.Web.Modules.Games.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Pages.Games;

[Authorize]
public class EditModel : GameFormPageModel
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

    public async Task<IActionResult> OnGetAsync(long id)
    {
        var game = await _db.Games.FindAsync([id], HttpContext.RequestAborted);
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
        Links = await _db.GamePlayLinks
            .Where(link => link.GameId == id)
            .OrderBy(link => link.SortOrder)
            .Select(link => new GamePlayLinkFormInput
            {
                DisplayName = link.DisplayName,
                Provider = link.Provider,
                TargetPlatform = link.TargetPlatform,
                Url = link.Url,
            })
            .ToListAsync(HttpContext.RequestAborted);
        EnsureLinkEditorRow();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(long id)
    {
        var ownerId = _currentUser.GetRequiredUserId();
        if (!ModelState.IsValid)
        {
            var ownsGame = await _db.Games.AnyAsync(
                game => game.Id == id && game.OwnerId == ownerId,
                HttpContext.RequestAborted);
            if (!ownsGame)
            {
                return NotFound();
            }

            EnsureLinkEditorRow();
            return Page();
        }

        var result = await _gameManagementService.UpdateAsync(
            id,
            ownerId,
            Title,
            Description,
            CreateLinkInputs(),
            HttpContext.RequestAborted);

        if (result.Outcome == UpdateGameOutcome.NotFound)
        {
            return NotFound();
        }

        if (result.Outcome == UpdateGameOutcome.AccountUnavailable)
        {
            return Challenge();
        }

        if (result.Outcome == UpdateGameOutcome.Invalid)
        {
            AddViolations(result.Violations);
            EnsureLinkEditorRow();
            return Page();
        }

        TempData["GameMessage"] = "수정되었습니다";
        return RedirectToPage("/Games/Detail", new { id });
    }
}
