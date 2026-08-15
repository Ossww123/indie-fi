using IndieFi.Web.Modules.KeyDistributions.Infrastructure;

namespace IndieFi.Web.Modules.KeyDistributions.Application;

public sealed class WaitingRoomKeyClaimAdmissionGate : IKeyClaimAdmissionGate
{
    private readonly IWaitingRoomStore _store;
    private readonly IWaitingRoomNotifier _notifier;

    public WaitingRoomKeyClaimAdmissionGate(
        IWaitingRoomStore store,
        IWaitingRoomNotifier notifier)
    {
        _store = store;
        _notifier = notifier;
    }

    public async Task<KeyClaimAdmissionOutcome> ValidateAsync(
        long dropId,
        long userId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _store.ValidateAdmissionAsync(
                dropId,
                userId,
                cancellationToken);
            return result.Outcome switch
            {
                WaitingRoomAdmissionValidationOutcome.Valid =>
                    KeyClaimAdmissionOutcome.Allowed,
                WaitingRoomAdmissionValidationOutcome.Terminal =>
                    KeyClaimAdmissionOutcome.Terminal,
                WaitingRoomAdmissionValidationOutcome.Missing
                    or WaitingRoomAdmissionValidationOutcome.NotAdmitted
                    or WaitingRoomAdmissionValidationOutcome.Expired =>
                    KeyClaimAdmissionOutcome.Required,
                _ => throw new InvalidOperationException(
                    $"Unsupported admission validation outcome: {result.Outcome}"),
            };
        }
        catch (WaitingRoomUnavailableException)
        {
            return KeyClaimAdmissionOutcome.Unavailable;
        }
    }

    public async Task CompleteAsync(
        long dropId,
        long userId,
        CancellationToken cancellationToken = default)
    {
        var completion = await _store.CompleteAsync(
            dropId,
            userId,
            cancellationToken);
        var admission = await _store.AdvanceAdmissionsAsync(
            dropId,
            cancellationToken);
        var changedUserIds = admission.PromotedUserIds
            .Concat(admission.ExpiredUserIds)
            .Append(userId)
            .Distinct()
            .ToArray();
        if (completion.Outcome != WaitingRoomCompletionOutcome.AlreadyCompleted
            || admission.PromotedUserIds.Count > 0
            || admission.ExpiredUserIds.Count > 0)
        {
            await _notifier.NotifyDropChangedAsync(dropId, cancellationToken);
            await _notifier.NotifyUsersChangedAsync(
                dropId,
                changedUserIds,
                cancellationToken);
        }
    }
}
