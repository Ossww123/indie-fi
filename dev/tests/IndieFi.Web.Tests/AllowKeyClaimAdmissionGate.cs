using IndieFi.Web.Modules.KeyDistributions.Application;

namespace IndieFi.Web.Tests;

internal sealed class AllowKeyClaimAdmissionGate : IKeyClaimAdmissionGate
{
    public static AllowKeyClaimAdmissionGate Instance { get; } = new();

    private AllowKeyClaimAdmissionGate()
    {
    }

    public Task<KeyClaimAdmissionOutcome> ValidateAsync(
        long dropId,
        long userId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(KeyClaimAdmissionOutcome.Allowed);

    public Task CompleteAsync(
        long dropId,
        long userId,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
