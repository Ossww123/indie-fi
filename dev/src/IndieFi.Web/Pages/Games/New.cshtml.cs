using System.ComponentModel.DataAnnotations;
using IndieFi.Web.Application.Abstractions;
using IndieFi.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IndieFi.Web.Pages.Games;

[Authorize]
public class NewModel : PageModel
{
    private readonly ICurrentUser _currentUser;
    private readonly GameManagementService _gameManagementService;

    public NewModel(
        ICurrentUser currentUser,
        GameManagementService gameManagementService)
    {
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

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        // The owner is taken from the signed cookie, never from the form;
        // otherwise anyone could register a game under someone else's id.
        var ownerId = _currentUser.GetRequiredUserId();
        var result = await _gameManagementService.CreateAsync(ownerId, Title, Description);

        // Redirect after a successful POST so refreshing the page does not resubmit.
        TempData["GameMessage"] = $"'{result.Title}' 등록 완료";
        return RedirectToPage("/Games/Index");
    }
}
