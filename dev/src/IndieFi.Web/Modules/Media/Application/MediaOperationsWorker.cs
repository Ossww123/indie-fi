using IndieFi.Web.Modules.Media.Infrastructure;
using Microsoft.Extensions.Options;

namespace IndieFi.Web.Modules.Media.Application;

public sealed class MediaOperationsWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MediaOperationsWorker> _logger;
    private readonly TimeSpan _interval;

    public MediaOperationsWorker(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        IOptions<MediaWorkerOptions> options,
        ILogger<MediaOperationsWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
        _interval = options.Value.ObservationInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var snapshot = await scope.ServiceProvider
                    .GetRequiredService<MediaOperationalSnapshotService>()
                    .GetAsync(_timeProvider.GetUtcNow(), stoppingToken);
                MediaTelemetry.UpdateOperationalSnapshot(snapshot);
                LogSnapshot(snapshot);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                MediaTelemetry.RecordOperationsObservationFailure();
                _logger.LogError(
                    exception,
                    "Media operations observation failed; the next interval will retry.");
            }

            await Task.Delay(_interval, _timeProvider, stoppingToken);
        }
    }

    private void LogSnapshot(MediaOperationalSnapshot snapshot)
    {
        if (snapshot.RecoveryDueCount > 0
            || snapshot.ExpiredLeaseCount > 0
            || snapshot.CleanupBacklogCount > 0)
        {
            _logger.LogWarning(
                "Media operations processing_pending={ProcessingPendingCount} "
                + "recovery_due={RecoveryDueCount} active_leases={ActiveLeaseCount} "
                + "expired_leases={ExpiredLeaseCount} cleanup_backlog={CleanupBacklogCount} "
                + "failed={FailedCount} oldest_recovery_due_age_seconds={OldestRecoveryDueAgeSeconds}",
                snapshot.ProcessingPendingCount,
                snapshot.RecoveryDueCount,
                snapshot.ActiveLeaseCount,
                snapshot.ExpiredLeaseCount,
                snapshot.CleanupBacklogCount,
                snapshot.FailedCount,
                snapshot.OldestRecoveryDueAgeSeconds);
        }
        else
        {
            _logger.LogDebug(
                "Media operations healthy processing_pending={ProcessingPendingCount} active_leases={ActiveLeaseCount}",
                snapshot.ProcessingPendingCount,
                snapshot.ActiveLeaseCount);
        }
    }
}
