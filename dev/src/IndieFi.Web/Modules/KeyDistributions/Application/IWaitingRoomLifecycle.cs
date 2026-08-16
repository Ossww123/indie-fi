namespace IndieFi.Web.Modules.KeyDistributions.Application;

public interface IWaitingRoomLifecycle
{
    Task SynchronizeTerminalAsync(
        long dropId,
        CancellationToken cancellationToken = default);

    Task NotifyInventoryChangedAsync(
        long dropId,
        CancellationToken cancellationToken = default);
}
