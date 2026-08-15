using IndieFi.Web.Modules.KeyDistributions.Infrastructure;

namespace IndieFi.Web.Modules.KeyDistributions.Application;

public sealed class WaitingRoomKeyClaimAdmissionGate : IKeyClaimAdmissionGate
{
    private readonly IWaitingRoomStore _store;

    public WaitingRoomKeyClaimAdmissionGate(IWaitingRoomStore store)
    {
        _store = store;
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
}
