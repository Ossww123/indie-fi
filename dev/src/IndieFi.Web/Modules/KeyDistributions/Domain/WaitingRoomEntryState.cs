namespace IndieFi.Web.Modules.KeyDistributions.Domain;

public enum WaitingRoomEntryState
{
    PreWaiting = 1,
    Waiting = 2,
    Admitted = 3,
    Completed = 4,
    Expired = 5,
    Terminal = 6,
}

public sealed record WaitingRoomStoredEntry(
    WaitingRoomEntryState State,
    long Order,
    long LastSeenUnixTimeMilliseconds,
    long? AdmissionExpiresUnixTimeMilliseconds = null)
{
    public WaitingRoomStoredEntry Validate()
    {
        if (!Enum.IsDefined(State))
        {
            throw new ArgumentOutOfRangeException(nameof(State));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(Order);
        ArgumentOutOfRangeException.ThrowIfNegative(LastSeenUnixTimeMilliseconds);
        if (AdmissionExpiresUnixTimeMilliseconds is long admissionExpires)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(admissionExpires);
        }

        if (State == WaitingRoomEntryState.Admitted
            != (AdmissionExpiresUnixTimeMilliseconds is not null))
        {
            throw new ArgumentException(
                "Only an admitted entry may have an admission expiry.");
        }

        return this;
    }
}
