namespace IndieFi.Web.Modules.Media.Domain;

public enum MediaStatus
{
    UploadPending,
    ProcessingPending,
    Processing,
    Ready,
    Failed,
    Expired,
    DeletePending,
    Deleted,
}

public static class MediaLifecyclePolicy
{
    public const int MaximumProcessingAttempts = 6;

    public static readonly TimeSpan UploadPendingLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan ProcessingLeaseLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan StagingSafetyLifetime = TimeSpan.FromDays(2);
    public static readonly TimeSpan TombstoneRetention = TimeSpan.FromDays(30);

    private static readonly TimeSpan[] ProcessingRetryDelays =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(6),
    ];

    public static bool CanTransition(MediaStatus current, MediaStatus next) =>
        (current, next) switch
        {
            (MediaStatus.UploadPending, MediaStatus.ProcessingPending) => true,
            (MediaStatus.UploadPending, MediaStatus.Failed) => true,
            (MediaStatus.UploadPending, MediaStatus.Expired) => true,
            (MediaStatus.UploadPending, MediaStatus.DeletePending) => true,

            (MediaStatus.ProcessingPending, MediaStatus.Processing) => true,
            (MediaStatus.ProcessingPending, MediaStatus.Failed) => true,
            (MediaStatus.ProcessingPending, MediaStatus.DeletePending) => true,

            (MediaStatus.Processing, MediaStatus.ProcessingPending) => true,
            (MediaStatus.Processing, MediaStatus.Ready) => true,
            (MediaStatus.Processing, MediaStatus.Failed) => true,
            (MediaStatus.Processing, MediaStatus.DeletePending) => true,

            (MediaStatus.Ready, MediaStatus.DeletePending) => true,
            (MediaStatus.DeletePending, MediaStatus.Deleted) => true,
            _ => false,
        };

    public static bool CanBePublic(MediaStatus status) => status == MediaStatus.Ready;

    public static bool IsTerminal(MediaStatus status) =>
        status is MediaStatus.Failed or MediaStatus.Expired or MediaStatus.Deleted;

    public static bool IsUploadExpired(
        DateTimeOffset createdAt,
        DateTimeOffset observedAt) =>
        observedAt >= createdAt + UploadPendingLifetime;

    public static bool TryGetProcessingRetryDelay(
        int completedAttemptCount,
        out TimeSpan retryDelay)
    {
        if (completedAttemptCount is < 1 or > MaximumProcessingAttempts)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedAttemptCount),
                $"The completed attempt count must be between 1 and {MaximumProcessingAttempts}.");
        }

        if (completedAttemptCount == MaximumProcessingAttempts)
        {
            retryDelay = default;
            return false;
        }

        retryDelay = ProcessingRetryDelays[completedAttemptCount - 1];
        return true;
    }
}
