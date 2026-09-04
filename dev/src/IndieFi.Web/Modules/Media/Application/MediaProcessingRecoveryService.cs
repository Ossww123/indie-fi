using IndieFi.Web.Data;
using IndieFi.Web.Modules.Media.Domain;
using IndieFi.Web.Modules.Media.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Media.Application;

public sealed class MediaProcessingRecoveryService
{
    private readonly AppDbContext _db;
    private readonly IMediaProcessingWorkExecutor _executor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MediaProcessingRecoveryService> _logger;

    public MediaProcessingRecoveryService(
        AppDbContext db,
        IMediaProcessingWorkExecutor executor,
        TimeProvider timeProvider,
        ILogger<MediaProcessingRecoveryService>? logger = null)
    {
        _db = db;
        _executor = executor;
        _timeProvider = timeProvider;
        _logger = logger
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<
                MediaProcessingRecoveryService>.Instance;
    }

    public async Task<int> RunBatchAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        var now = _timeProvider.GetUtcNow();
        var candidates = await _db.MediaAssets
            .AsNoTracking()
            .Where(media =>
                (media.Status == MediaStatus.ProcessingPending
                    && (media.NextProcessingAttemptAt == null
                        || media.NextProcessingAttemptAt <= now))
                || (media.Status == MediaStatus.Processing
                    && media.ProcessingLeaseExpiresAt <= now))
            .OrderBy(media => media.NextProcessingAttemptAt
                ?? media.ProcessingLeaseExpiresAt
                ?? media.UpdatedAt)
            .ThenBy(media => media.Id)
            .Select(media => new Candidate(media.Id, media.Revision))
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            try
            {
                await _executor.ExecuteAsync(
                    candidate.MediaId,
                    candidate.Revision,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Media processing recovery failed mediaId={MediaId} revision={Revision}",
                    candidate.MediaId,
                    candidate.Revision);
            }
        }

        MediaTelemetry.RecordRecoveryBatch(candidates.Count);
        return candidates.Count;
    }

    private sealed record Candidate(Guid MediaId, int Revision);
}
