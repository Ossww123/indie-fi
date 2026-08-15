namespace IndieFi.Web.Modules.KeyDistributions.Application;

public sealed class WaitingRoomLifecycle : IWaitingRoomLifecycle
{
    private readonly IWaitingRoomStore _store;
    private readonly IWaitingRoomNotifier _notifier;
    private readonly ILogger<WaitingRoomLifecycle> _logger;

    public WaitingRoomLifecycle(
        IWaitingRoomStore store,
        IWaitingRoomNotifier notifier,
        ILogger<WaitingRoomLifecycle> logger)
    {
        _store = store;
        _notifier = notifier;
        _logger = logger;
    }

    public async Task SynchronizeTerminalAsync(
        long dropId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _store.TerminateAsync(dropId, cancellationToken);
            if (result.Outcome == WaitingRoomTerminationOutcome.Terminated)
            {
                await _notifier.NotifyDropChangedAsync(dropId, cancellationToken);
                await _notifier.NotifyUsersChangedAsync(
                    dropId,
                    result.AffectedUserIds,
                    cancellationToken);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Waiting-room terminal synchronization failed after PostgreSQL commit drop={DropId}",
                dropId);
        }
    }

    public Task NotifyInventoryChangedAsync(
        long dropId,
        CancellationToken cancellationToken = default) =>
        _notifier.NotifyDropChangedAsync(dropId, cancellationToken);
}
