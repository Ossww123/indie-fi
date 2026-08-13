using System.ComponentModel.DataAnnotations;
using IndieFi.Web.Application.Abstractions;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IndieFi.Web.Pages.Games;

[Authorize]
public class NewModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _currentUser;

    public NewModel(AppDbContext db, ICurrentUser currentUser)
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

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        // The owner is taken from the signed cookie, never from the form;
        // otherwise anyone could register a game under someone else's id.
        var ownerId = _currentUser.GetRequiredUserId();
        var description = string.IsNullOrWhiteSpace(Description) ? null : Description;

        var game = new Game
        {
            OwnerId = ownerId,
            Title = Title,
            Description = description,
        };

        _db.Games.Add(game);
        await _db.SaveChangesAsync();

        // Redirect after a successful POST so refreshing the page does not resubmit.
        TempData["GameMessage"] = $"'{game.Title}' 등록 완료";
        return RedirectToPage("/Games/Index");
    }
}
