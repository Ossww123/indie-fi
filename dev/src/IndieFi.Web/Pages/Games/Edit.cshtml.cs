using System.ComponentModel.DataAnnotations;
using IndieFi.Web.Application.Abstractions;
using IndieFi.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IndieFi.Web.Pages.Games;

[Authorize]
public class EditModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _currentUser;

    public EditModel(AppDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
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

        var game = await _db.Games.FindAsync(id);
        var ownerId = _currentUser.GetRequiredUserId();

        // The ownership check must be repeated on POST: an attacker can send this
        // POST directly without ever loading the GET page. Hiding the edit link in
        // the UI is not a security boundary — this server check is.
        if (game is null || game.OwnerId != ownerId)
        {
            return NotFound();
        }

        game.Title = Title;
        game.Description = string.IsNullOrWhiteSpace(Description) ? null : Description;
        await _db.SaveChangesAsync();

        TempData["GameMessage"] = "수정되었습니다";
        return RedirectToPage("/Games/Detail", new { id });
    }
}
