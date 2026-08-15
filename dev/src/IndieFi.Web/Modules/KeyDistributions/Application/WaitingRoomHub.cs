using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace IndieFi.Web.Modules.KeyDistributions.Application;

[Authorize]
public sealed class WaitingRoomHub : Hub
{
    private readonly WaitingRoomParticipationService _participationService;

    public WaitingRoomHub(WaitingRoomParticipationService participationService)
    {
        _participationService = participationService;
    }

    public async Task SubscribeAsync(long dropId)
    {
        if (dropId <= 0 || !TryGetUserId(Context.User, out var userId))
        {
            throw new HubException("대기실 구독 요청이 올바르지 않습니다.");
        }

        var participation = await _participationService.GetAsync(
            dropId,
            userId,
            Context.ConnectionAborted);
        if (participation.Outcome is not (
            WaitingRoomParticipationOutcome.Available
            or WaitingRoomParticipationOutcome.Existing
            or WaitingRoomParticipationOutcome.Completed))
        {
            throw new HubException("이 배포의 대기실을 구독할 수 없습니다.");
        }

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            WaitingRoomHubGroups.Drop(dropId),
            Context.ConnectionAborted);
    }

    private static bool TryGetUserId(ClaimsPrincipal? principal, out long userId)
    {
        userId = 0;
        var values = principal?.FindAll(ClaimTypes.NameIdentifier).ToArray() ?? [];
        return values.Length == 1
            && long.TryParse(
                values[0].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out userId)
            && userId > 0;
    }
}

public static class WaitingRoomHubGroups
{
    public static string Drop(long dropId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dropId);
        return $"waiting-room:drop:{dropId.ToString(CultureInfo.InvariantCulture)}";
    }
}
