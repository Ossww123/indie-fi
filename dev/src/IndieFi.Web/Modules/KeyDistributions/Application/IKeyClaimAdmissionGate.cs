namespace IndieFi.Web.Modules.KeyDistributions.Application;

public interface IKeyClaimAdmissionGate
{
    Task<KeyClaimAdmissionOutcome> ValidateAsync(
        long dropId,
        long userId,
        CancellationToken cancellationToken = default);
}

public enum KeyClaimAdmissionOutcome
{
    Allowed,
    Required,
    Terminal,
    Unavailable,
}
