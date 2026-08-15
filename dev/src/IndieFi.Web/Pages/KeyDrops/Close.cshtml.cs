using IndieFi.Web.Application.Abstractions;
using IndieFi.Web.Data;
using IndieFi.Web.Modules.KeyDistributions.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Pages.KeyDrops;

[Authorize]
public sealed class CloseModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly KeyDropCloseService _keyDropCloseService;
    private readonly PublicKeyDropReader _publicKeyDropReader;

    public CloseModel(
        AppDbContext db,
        ICurrentUser currentUser,
        KeyDropCloseService keyDropCloseService,
        PublicKeyDropReader publicKeyDropReader)
    {
        _db = db;
        _currentUser = currentUser;
        _keyDropCloseService = keyDropCloseService;
        _publicKeyDropReader = publicKeyDropReader;
    }

    public PublicKeyDropDetail Drop { get; private set; } = null!;

    public Task<IActionResult> OnGetAsync(long id) =>
        LoadPageAsync(id, _currentUser.GetRequiredUserId());

    public async Task<IActionResult> OnPostAsync(long id)
    {
        var ownerId = _currentUser.GetRequiredUserId();
        var result = await _keyDropCloseService.CloseAsync(
            id,
            ownerId,
            HttpContext.RequestAborted);

        if (result.Outcome is
            CloseKeyDropOutcome.DropNotFound or
            CloseKeyDropOutcome.NotOwner)
        {
            return NotFound();
        }

        if (result.Outcome == CloseKeyDropOutcome.AccountUnavailable)
        {
            return Challenge();
        }

        if (result.Outcome == CloseKeyDropOutcome.Closed)
        {
            return RedirectToPage(
                "/KeyDrops/Detail",
                new { id, closure = "closed" });
        }

        if (result.Outcome == CloseKeyDropOutcome.AlreadyClosed)
        {
            return RedirectToPage(
                "/KeyDrops/Detail",
                new { id, closure = "already" });
        }

        ModelState.AddModelError(
            string.Empty,
            result.Outcome == CloseKeyDropOutcome.Conflict
                ? "배포 상태가 변경되었습니다. 최신 상태를 확인한 뒤 다시 시도해 주세요."
                : "예정 또는 진행 중인 배포만 조기 마감할 수 있습니다.");

        return await LoadPageAsync(id, ownerId);
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
