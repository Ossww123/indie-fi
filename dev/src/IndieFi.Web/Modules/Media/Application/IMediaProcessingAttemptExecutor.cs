namespace IndieFi.Web.Modules.Media.Application;

public enum MediaProcessingAttemptOutcome
{
    Completed,
    TransientFailure,
    PermanentFailure,
}

public sealed record MediaProcessingAttemptResult
{
    private MediaProcessingAttemptResult(
        MediaProcessingAttemptOutcome outcome,
        string? failureCode = null)
    {
        Outcome = outcome;
        FailureCode = failureCode;
    }

    public MediaProcessingAttemptOutcome Outcome { get; }

    public string? FailureCode { get; }

    public static MediaProcessingAttemptResult Completed() =>
        new(MediaProcessingAttemptOutcome.Completed);

    public static MediaProcessingAttemptResult TransientFailure(
        string failureCode)
    {
        MediaProcessingFailureCode.RequireValid(failureCode);
        return new(
            MediaProcessingAttemptOutcome.TransientFailure,
            failureCode);
    }

    public static MediaProcessingAttemptResult PermanentFailure(
        string failureCode)
    {
        MediaProcessingFailureCode.RequireValid(failureCode);
        return new(
            MediaProcessingAttemptOutcome.PermanentFailure,
            failureCode);
    }
}

public interface IMediaProcessingAttemptExecutor
{
    Task<MediaProcessingAttemptResult> ExecuteAsync(
        MediaProcessingLease lease,
        CancellationToken cancellationToken = default);
}
