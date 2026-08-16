using IndieFi.Web.Application.Abstractions;
using IndieFi.Web.Modules.Games.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IndieFi.Web.Pages.Games;

[Authorize]
public class NewModel : GameFormPageModel
{
    private readonly ICurrentUser _currentUser;
    private readonly GameManagementService _gameManagementService;
    private readonly GameClassificationCatalogQueryService _classificationCatalog;

    public NewModel(
        ICurrentUser currentUser,
        GameManagementService gameManagementService,
        GameClassificationCatalogQueryService classificationCatalog)
    {
        _currentUser = currentUser;
        _gameManagementService = gameManagementService;
        _classificationCatalog = classificationCatalog;
    }

    public async Task OnGetAsync()
    {
        await LoadActiveClassificationCatalogAsync(
            _classificationCatalog,
            HttpContext.RequestAborted);
        EnsureLinkEditorRow();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            await LoadActiveClassificationCatalogAsync(
                _classificationCatalog,
                HttpContext.RequestAborted);
            EnsureLinkEditorRow();
            return Page();
        }

        // The owner is taken from the signed cookie, never from the form;
        // otherwise anyone could register a game under someone else's id.
        var ownerId = _currentUser.GetRequiredUserId();
        var result = await _gameManagementService.CreateAsync(
            ownerId,
            Title,
            Description,
            CreateClassificationInput(),
            CreateLinkInputs(),
            HttpContext.RequestAborted);

        if (result.Outcome == CreateGameOutcome.Invalid)
        {
            AddViolations(result.Violations);
            await LoadActiveClassificationCatalogAsync(
                _classificationCatalog,
                HttpContext.RequestAborted);
            EnsureLinkEditorRow();
            return Page();
        }

        if (result.Outcome == CreateGameOutcome.AccountUnavailable)
        {
            return Challenge();
        }

        // Redirect after a successful POST so refreshing the page does not resubmit.
        TempData["GameMessage"] = $"'{result.Title}' 등록 완료";
        return RedirectToPage("/Games/Index");
    }
}
