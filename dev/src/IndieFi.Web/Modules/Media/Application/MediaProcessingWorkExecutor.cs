namespace IndieFi.Web.Modules.Media.Application;

public sealed class MediaProcessingWorkExecutor : IMediaProcessingWorkExecutor
{
    private const string UnexpectedFailureCode = "processing_unexpected_error";
    private const string IncompleteFailureCode = "processing_incomplete";

    private readonly MediaProcessingLeaseService _leases;
    private readonly IMediaProcessingAttemptExecutor _attemptExecutor;
    private readonly ILogger<MediaProcessingWorkExecutor> _logger;

    public MediaProcessingWorkExecutor(
        MediaProcessingLeaseService leases,
        IMediaProcessingAttemptExecutor attemptExecutor,
        ILogger<MediaProcessingWorkExecutor> logger)
    {
        _leases = leases;
        _attemptExecutor = attemptExecutor;
        _logger = logger;
    }

    public async Task ExecuteAsync(
        Guid mediaId,
        int revision,
        CancellationToken cancellationToken = default)
    {
        var acquisition = await _leases.TryAcquireAsync(
            mediaId,
            revision,
            cancellationToken);
        if (acquisition.Lease is not { } lease)
        {
            _logger.LogInformation(
                "Media processing lease mediaId={MediaId} revision={Revision} outcome={Outcome}",
                mediaId,
                revision,
                acquisition.Outcome);
            return;
        }

        MediaProcessingAttemptResult result;
        try
        {
            result = await _attemptExecutor.ExecuteAsync(lease, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Media processing attempt crashed mediaId={MediaId} revision={Revision} attempt={Attempt}",
                mediaId,
                revision,
                lease.AttemptNumber);
            result = MediaProcessingAttemptResult.TransientFailure(
                UnexpectedFailureCode);
        }

        var failureOutcome = await ApplyResultAsync(
            lease,
            result,
            cancellationToken);
        _logger.LogInformation(
            "Media processing attempt mediaId={MediaId} revision={Revision} attempt={Attempt} outcome={Outcome} failureCode={FailureCode} failureOutcome={FailureOutcome}",
            mediaId,
            revision,
            lease.AttemptNumber,
            result.Outcome,
            result.FailureCode,
            failureOutcome);
    }

    private async Task<MediaProcessingFailureOutcome?> ApplyResultAsync(
        MediaProcessingLease lease,
        MediaProcessingAttemptResult result,
        CancellationToken cancellationToken)
    {
        switch (result.Outcome)
        {
            case MediaProcessingAttemptOutcome.Completed:
                if (!await _leases.IsLeaseActiveAsync(lease, cancellationToken))
                {
                    return null;
                }

                return await _leases.RecordTransientFailureAsync(
                    lease,
                    IncompleteFailureCode,
                    cancellationToken);
            case MediaProcessingAttemptOutcome.TransientFailure:
                return await _leases.RecordTransientFailureAsync(
                    lease,
                    RequireFailureCode(result),
                    cancellationToken);
            case MediaProcessingAttemptOutcome.PermanentFailure:
                return await _leases.RecordPermanentFailureAsync(
                    lease,
                    RequireFailureCode(result),
                    cancellationToken);
            default:
                throw new InvalidOperationException(
                    $"Unknown media processing outcome: {result.Outcome}.");
        }
    }

    private static string RequireFailureCode(MediaProcessingAttemptResult result)
    {
        if (!MediaProcessingFailureCode.IsValid(result.FailureCode))
        {
            throw new InvalidOperationException(
                "A failed media processing attempt requires a valid failure code.");
        }

        return result.FailureCode!;
    }
}
