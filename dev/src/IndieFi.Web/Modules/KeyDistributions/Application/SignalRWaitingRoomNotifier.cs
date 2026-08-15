using System.Globalization;
using Microsoft.AspNetCore.SignalR;

namespace IndieFi.Web.Modules.KeyDistributions.Application;

public sealed class SignalRWaitingRoomNotifier : IWaitingRoomNotifier
{
    public const string DropChangedMethod = "WaitingRoomDropChanged";
    public const string UserChangedMethod = "WaitingRoomUserChanged";

    private readonly IHubContext<WaitingRoomHub> _hubContext;
    private readonly ILogger<SignalRWaitingRoomNotifier> _logger;

    public SignalRWaitingRoomNotifier(
        IHubContext<WaitingRoomHub> hubContext,
        ILogger<SignalRWaitingRoomNotifier> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public Task NotifyDropChangedAsync(
        long dropId,
        CancellationToken cancellationToken = default) =>
        SendBestEffortAsync(
            _hubContext.Clients.Group(WaitingRoomHubGroups.Drop(dropId)),
            DropChangedMethod,
            new WaitingRoomChangedMessage(dropId),
            dropId,
            cancellationToken);

    public Task NotifyUsersChangedAsync(
        long dropId,
        IReadOnlyCollection<long> userIds,
        CancellationToken cancellationToken = default)
    {
        var identifiers = userIds
            .Where(userId => userId > 0)
            .Distinct()
            .Select(userId => userId.ToString(CultureInfo.InvariantCulture))
            .ToArray();
        return identifiers.Length == 0
            ? Task.CompletedTask
            : SendBestEffortAsync(
                _hubContext.Clients.Users(identifiers),
                UserChangedMethod,
                new WaitingRoomChangedMessage(dropId),
                dropId,
                cancellationToken);
    }

    private async Task SendBestEffortAsync(
        IClientProxy clients,
        string method,
        WaitingRoomChangedMessage message,
        long dropId,
        CancellationToken cancellationToken)
    {
        try
        {
            await clients.SendAsync(method, message, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Waiting-room notification failed method={Method} drop={DropId}",
                method,
                dropId);
        }
    }
}

public sealed record WaitingRoomChangedMessage(long DropId);
