namespace IndieFi.Web.Modules.KeyDistributions.Application;

public interface IWaitingRoomNotifier
{
    Task NotifyDropChangedAsync(
        long dropId,
        CancellationToken cancellationToken = default);

    Task NotifyUsersChangedAsync(
        long dropId,
        IReadOnlyCollection<long> userIds,
        CancellationToken cancellationToken = default);
}
