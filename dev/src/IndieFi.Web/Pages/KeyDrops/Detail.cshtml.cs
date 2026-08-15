using IndieFi.Web.Application.Abstractions;
using IndieFi.Web.Modules.KeyDistributions.Application;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IndieFi.Web.Pages.KeyDrops;

public class DetailModel : PageModel
{
    private sealed record ClaimNotice(ClaimOutcome Outcome, string Code, string Message);

    private static readonly ClaimNotice[] ClaimNotices =
    [
        new(ClaimOutcome.NotOpen, "notopen", "아직 오픈되지 않았습니다."),
        new(ClaimOutcome.SoldOut, "soldout", "키가 모두 소진되었습니다."),
        new(ClaimOutcome.ClosedEarly, "closed", "조기 마감된 배포입니다."),
        new(ClaimOutcome.Conflict, "conflict", "동시 요청으로 키를 발급하지 못했습니다. 잠시 후 다시 시도해 주세요."),
    ];

    private readonly PublicKeyDropReader _publicKeyDropReader;
    private readonly ICurrentUser _currentUser;
    private readonly KeyClaimService _keyClaimService;
    private readonly GrantedGameKeyReader _grantedGameKeyReader;

    public DetailModel(
        PublicKeyDropReader publicKeyDropReader,
        ICurrentUser currentUser,
        KeyClaimService keyClaimService,
        GrantedGameKeyReader grantedGameKeyReader)
    {
        _publicKeyDropReader = publicKeyDropReader;
        _currentUser = currentUser;
        _keyClaimService = keyClaimService;
        _grantedGameKeyReader = grantedGameKeyReader;
    }

    public PublicKeyDropDetail Drop { get; private set; } = null!;
    public string? ClaimedKey { get; private set; }
    public string? ClaimMessage { get; private set; }
    public string? InventoryMessage { get; private set; }
    public string? ClosureMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(
        long id,
        string? claim,
        string? inventory,
        string? closure)
    {
        // Query strings are user-controlled, so only known codes may select displayed text.
        ClaimMessage = ClaimNotices
            .FirstOrDefault(notice => notice.Code == claim)
            ?.Message;
        InventoryMessage = inventory == "added"
            ? "게임 키 재고를 추가했습니다."
            : null;
        ClosureMessage = closure switch
        {
            "closed" => "배포를 조기 마감했습니다.",
            "already" => "이미 조기 마감된 배포입니다.",
            _ => null,
        };

        var drop = await _publicKeyDropReader.GetAsync(
            id,
            HttpContext.RequestAborted);

        if (drop is null)
        {
            return NotFound();
        }

        Drop = drop;

        if (_currentUser.IsAuthenticated)
        {
            var userId = _currentUser.GetRequiredUserId();

            ClaimedKey = await _grantedGameKeyReader.GetForUserAsync(id, userId);
            if (ClaimedKey is not null)
            {
                Response.Headers.CacheControl = "private, no-store";
                Response.Headers.Pragma = "no-cache";
            }
        }

        return Page();
    }

    public async Task<IActionResult> OnPostClaimAsync(long id)
    {
        // The page itself stays public, so claiming is guarded here instead.
        // [Authorize] cannot do it: Razor Pages ignores the attribute on a handler.
        if (!_currentUser.IsAuthenticated)
        {
            return Challenge();
        }

        var userId = _currentUser.GetRequiredUserId();
        var result = await _keyClaimService.ClaimAsync(
            id,
            userId,
            HttpContext.RequestAborted);

        if (result.Outcome == ClaimOutcome.DropNotFound)
        {
            return NotFound();
        }

        if (result.Outcome == ClaimOutcome.AccountUnavailable)
        {
            return Challenge();
        }

        var claimNotice = ClaimNotices
            .FirstOrDefault(notice => notice.Outcome == result.Outcome);

        if (claimNotice is not null)
        {
            return RedirectToPage(new { id, claim = claimNotice.Code });
        }

        return RedirectToPage(new { id });
    }
}
