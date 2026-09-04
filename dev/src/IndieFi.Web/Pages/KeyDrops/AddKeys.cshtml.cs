using IndieFi.Web.Application.Abstractions;
using IndieFi.Web.Data;
using IndieFi.Web.Modules.KeyDistributions.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Pages.KeyDrops;

[Authorize]
public sealed class AddKeysModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly KeyDropInventoryService _keyDropInventoryService;
    private readonly PublicKeyDropReader _publicKeyDropReader;

    public AddKeysModel(
        AppDbContext db,
        ICurrentUser currentUser,
        KeyDropInventoryService keyDropInventoryService,
        PublicKeyDropReader publicKeyDropReader)
    {
        _db = db;
        _currentUser = currentUser;
        _keyDropInventoryService = keyDropInventoryService;
        _publicKeyDropReader = publicKeyDropReader;
    }

    public PublicKeyDropDetail Drop { get; private set; } = null!;

    [BindProperty]
    public string? KeysText { get; set; }

    public Task<IActionResult> OnGetAsync(long id) =>
        LoadPageAsync(id, _currentUser.GetRequiredUserId());

    public async Task<IActionResult> OnPostAsync(long id)
    {
        var ownerId = _currentUser.GetRequiredUserId();

        if (!ModelState.IsValid)
        {
            ClearSubmittedKeys();
            return await LoadPageAsync(id, ownerId);
        }

        var result = await _keyDropInventoryService.AddAsync(
            id,
            ownerId,
            KeysText,
            HttpContext.RequestAborted);

        if (result.Outcome is
            AddKeyInventoryOutcome.DropNotFound or
            AddKeyInventoryOutcome.NotOwner)
        {
            return NotFound();
        }

        if (result.Outcome == AddKeyInventoryOutcome.AccountUnavailable)
        {
            return Challenge();
        }

        if (result.Outcome == AddKeyInventoryOutcome.InvalidStatus)
        {
            ModelState.AddModelError(
                string.Empty,
                "예정 또는 진행 중인 배포에만 키를 추가할 수 있습니다.");
        }
        else if (result.Outcome == AddKeyInventoryOutcome.Conflict)
        {
            ModelState.AddModelError(
                string.Empty,
                "배포 상태가 변경되었습니다. 최신 상태를 확인한 뒤 다시 시도해 주세요.");
        }

        foreach (var violation in result.Violations)
        {
            ModelState.AddModelError(violation.Field, violation.Message);
        }

        if (!ModelState.IsValid)
        {
            // Submitted game keys are secrets. Do not echo them into the response.
            ClearSubmittedKeys();
            return await LoadPageAsync(id, ownerId);
        }

        return RedirectToPage(
            "/KeyDrops/Detail",
            new { id, inventory = "added" });
    }

    private void ClearSubmittedKeys()
    {
        KeysText = null;
        ModelState.SetModelValue(
            nameof(KeysText),
            rawValue: string.Empty,
            attemptedValue: string.Empty);
    }

    private async Task<IActionResult> LoadPageAsync(long dropId, long ownerId)
    {
        var actualOwnerId = await _db.KeyDrops
            .Where(drop => drop.Id == dropId)
            .Select(drop => (long?)drop.OwnerId)
            .SingleOrDefaultAsync(HttpContext.RequestAborted);

        if (actualOwnerId != ownerId)
        {
            return NotFound();
        }

        var drop = await _publicKeyDropReader.GetAsync(
            dropId,
            HttpContext.RequestAborted);
        if (drop is null)
        {
            return NotFound();
        }

        Drop = drop;
        return Page();
    }
}
