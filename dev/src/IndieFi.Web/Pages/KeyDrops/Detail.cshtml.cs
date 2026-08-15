using IndieFi.Web.Application.Abstractions;
using IndieFi.Web.Modules.KeyDistributions.Application;
using IndieFi.Web.Modules.KeyDistributions.Domain;
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
        new(ClaimOutcome.NotEligible, "ineligible", "이 배포의 참여 자격을 충족하지 못했습니다."),
        new(ClaimOutcome.AdmissionRequired, "admission", "유효한 대기실 입장 허용이 필요합니다."),
        new(ClaimOutcome.WaitingRoomTerminal, "waitingclosed", "대기실이 종료되어 키를 받을 수 없습니다."),
        new(ClaimOutcome.WaitingRoomUnavailable, "waitingunavailable", "대기실에 연결할 수 없습니다. 잠시 후 다시 시도해 주세요."),
        new(ClaimOutcome.Conflict, "conflict", "동시 요청으로 키를 발급하지 못했습니다. 잠시 후 다시 시도해 주세요."),
    ];

    private static readonly IReadOnlyDictionary<string, string> WaitingNotices =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["joined"] = "대기실에 들어왔습니다.",
            ["existing"] = "기존 대기 상태를 복구했습니다.",
            ["rejoined"] = "재연결 유예시간이 지나 현재 대기열의 마지막 순서로 다시 들어왔습니다.",
            ["notopen"] = "대기실은 배포 오픈 30분 전부터 이용할 수 있습니다.",
            ["ineligible"] = "이 배포의 참여 자격을 충족하지 못했습니다.",
            ["soldout"] = "키가 모두 소진되어 대기실이 종료되었습니다.",
            ["closed"] = "조기 마감되어 대기실이 종료되었습니다.",
            ["terminal"] = "대기실이 종료되었습니다.",
            ["unavailable"] = "대기실에 연결할 수 없습니다. 잠시 후 다시 시도해 주세요.",
            ["completed"] = "이미 이 배포의 키를 받았습니다.",
        };

    private readonly PublicKeyDropReader _publicKeyDropReader;
    private readonly ICurrentUser _currentUser;
    private readonly KeyClaimService _keyClaimService;
    private readonly GrantedGameKeyReader _grantedGameKeyReader;
    private readonly WaitingRoomParticipationService _waitingRoomParticipationService;

    public DetailModel(
        PublicKeyDropReader publicKeyDropReader,
        ICurrentUser currentUser,
        KeyClaimService keyClaimService,
        GrantedGameKeyReader grantedGameKeyReader,
        WaitingRoomParticipationService waitingRoomParticipationService)
    {
        _publicKeyDropReader = publicKeyDropReader;
        _currentUser = currentUser;
        _keyClaimService = keyClaimService;
        _grantedGameKeyReader = grantedGameKeyReader;
        _waitingRoomParticipationService = waitingRoomParticipationService;
    }

    public PublicKeyDropDetail Drop { get; private set; } = null!;
    public string? ClaimedKey { get; private set; }
    public string? ClaimMessage { get; private set; }
    public string? InventoryMessage { get; private set; }
    public string? ClosureMessage { get; private set; }
    public string? WaitingRoomMessage { get; private set; }
    public WaitingRoomParticipationResult? WaitingRoom { get; private set; }

    public async Task<IActionResult> OnGetAsync(
        long id,
        string? claim,
        string? inventory,
        string? closure,
        string? waiting)
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
        WaitingRoomMessage = waiting is not null
            && WaitingNotices.TryGetValue(waiting, out var waitingMessage)
                ? waitingMessage
                : null;

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

            WaitingRoom = await _waitingRoomParticipationService.GetAsync(
                id,
                userId,
                HttpContext.RequestAborted);
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

    public async Task<IActionResult> OnPostJoinAsync(long id)
    {
        if (!_currentUser.IsAuthenticated)
        {
            return Challenge();
        }

        var result = await _waitingRoomParticipationService.JoinAsync(
            id,
            _currentUser.GetRequiredUserId(),
            HttpContext.RequestAborted);
        if (result.Outcome == WaitingRoomParticipationOutcome.DropNotFound)
        {
            return NotFound();
        }

        if (result.Outcome == WaitingRoomParticipationOutcome.AccountUnavailable)
        {
            return Challenge();
        }

        var code = result.Outcome switch
        {
            WaitingRoomParticipationOutcome.Joined => "joined",
            WaitingRoomParticipationOutcome.Existing => "existing",
            WaitingRoomParticipationOutcome.Rejoined => "rejoined",
            WaitingRoomParticipationOutcome.Completed => "completed",
            WaitingRoomParticipationOutcome.NotYetOpen => "notopen",
            WaitingRoomParticipationOutcome.NotEligible => "ineligible",
            WaitingRoomParticipationOutcome.SoldOut => "soldout",
            WaitingRoomParticipationOutcome.ClosedEarly => "closed",
            WaitingRoomParticipationOutcome.Terminal => "terminal",
            WaitingRoomParticipationOutcome.Unavailable => "unavailable",
            WaitingRoomParticipationOutcome.Available => throw new InvalidOperationException(
                "A join operation cannot finish as merely available."),
            _ => throw new InvalidOperationException(
                $"Unsupported waiting-room participation outcome: {result.Outcome}"),
        };
        return RedirectToPage(new { id, waiting = code });
    }

    public string GetWaitingRoomStateText() => WaitingRoom?.Entry?.State switch
    {
        WaitingRoomEntryState.PreWaiting => "사전 대기 중",
        WaitingRoomEntryState.Waiting => "대기 중",
        WaitingRoomEntryState.Admitted => "입장 가능",
        WaitingRoomEntryState.Completed => "지급 완료",
        WaitingRoomEntryState.Expired => "입장 만료",
        WaitingRoomEntryState.Terminal => "대기실 종료",
        _ => string.Empty,
    };

    public DateTimeOffset? GetAdmissionExpiresAt()
    {
        var unixTimeMilliseconds = WaitingRoom?.Entry?.AdmissionExpiresUnixTimeMilliseconds;
        return unixTimeMilliseconds is null
            ? null
            : DateTimeOffset.FromUnixTimeMilliseconds(unixTimeMilliseconds.Value);
    }
}
