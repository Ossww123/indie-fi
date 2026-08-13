using IndieFi.Web.Application.Abstractions;
using IndieFi.Web.Modules.Games.Application;
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

    // No validation annotations here: the service decides whether the input is usable.
    // Duplicating a rule as an annotation would fail model binding first and return
    // before the service ever runs, hiding every other problem with the form.
    [BindProperty]
    public string? Title { get; set; }

    [BindProperty]
    public string? Description { get; set; }

    public async Task<IActionResult> OnPostAsync()
    {
        // The owner is taken from the signed cookie, never from the form;
        // otherwise anyone could register a game under someone else's id.
        var ownerId = _currentUser.GetRequiredUserId();
        var result = await _gameManagementService.CreateAsync(ownerId, Title, Description);

        if (result.Outcome == CreateGameOutcome.Invalid)
        {
            foreach (var violation in result.Violations)
            {
                ModelState.AddModelError(violation.Field, violation.Message);
            }

            return Page();
        }

        // Redirect after a successful POST so refreshing the page does not resubmit.
        TempData["GameMessage"] = $"'{result.Title}' 등록 완료";
        return RedirectToPage("/Games/Index");
    }
}
