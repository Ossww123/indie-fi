using IndieFi.Web.Modules.Media.Application;
using Microsoft.Extensions.Options;

namespace IndieFi.Web.Modules.Media.Infrastructure;

public sealed class MediaProcessingRecoveryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MediaProcessingRecoveryWorker> _logger;
    private readonly TimeSpan _interval;
    private readonly int _batchSize;

    public MediaProcessingRecoveryWorker(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        IOptions<MediaWorkerOptions> options,
        ILogger<MediaProcessingRecoveryWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
        _interval = options.Value.RecoveryInterval;
        _batchSize = options.Value.RecoveryBatchSize;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider
                    .GetRequiredService<MediaProcessingRecoveryService>()
                    .RunBatchAsync(_batchSize, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Media processing recovery batch failed.");
            }

            await Task.Delay(_interval, _timeProvider, stoppingToken);
        }
    }
}
