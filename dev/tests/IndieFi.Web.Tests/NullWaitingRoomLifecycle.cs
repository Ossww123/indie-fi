using IndieFi.Web.Modules.KeyDistributions.Application;

namespace IndieFi.Web.Tests;

internal sealed class NullWaitingRoomLifecycle : IWaitingRoomLifecycle
{
    public static NullWaitingRoomLifecycle Instance { get; } = new();

    private NullWaitingRoomLifecycle()
    {
    }

    public Task SynchronizeTerminalAsync(
        long dropId,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task NotifyInventoryChangedAsync(
        long dropId,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

internal sealed class RecordingWaitingRoomLifecycle : IWaitingRoomLifecycle
{
    public List<long> TerminalDropIds { get; } = [];

    public List<long> InventoryChangedDropIds { get; } = [];

    public Task SynchronizeTerminalAsync(
        long dropId,
        CancellationToken cancellationToken = default)
    {
        TerminalDropIds.Add(dropId);
        return Task.CompletedTask;
    }

    public Task NotifyInventoryChangedAsync(
        long dropId,
        CancellationToken cancellationToken = default)
    {
        InventoryChangedDropIds.Add(dropId);
        return Task.CompletedTask;
    }
}
