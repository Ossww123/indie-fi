using IndieFi.Web.Modules.KeyDistributions.Application;

namespace IndieFi.Web.Tests;

internal sealed class NullWaitingRoomNotifier : IWaitingRoomNotifier
{
    public static NullWaitingRoomNotifier Instance { get; } = new();

    private NullWaitingRoomNotifier()
    {
    }

    public Task NotifyDropChangedAsync(
        long dropId,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task NotifyUsersChangedAsync(
        long dropId,
        IReadOnlyCollection<long> userIds,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
