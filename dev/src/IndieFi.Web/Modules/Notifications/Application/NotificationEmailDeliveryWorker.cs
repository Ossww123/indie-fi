using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IndieFi.Web.Modules.Notifications.Application;

public sealed class NotificationEmailDeliveryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NotificationEmailDeliveryWorker> _logger;
    private readonly bool _enabled;
    private readonly TimeSpan _interval;
    private readonly int _batchSize;

    public NotificationEmailDeliveryWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<NotificationEmailDeliveryWorker> logger,
        bool enabled,
        TimeSpan interval,
        int batchSize)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _enabled = enabled;
        _interval = interval;
        _batchSize = batchSize;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var processor = scope.ServiceProvider
                    .GetRequiredService<NotificationEmailDeliveryProcessor>();
                var result = await processor.ProcessBatchAsync(
                    _batchSize,
                    stoppingToken);
                if (result.SentCount > 0
                    || result.ExpiredCount > 0
                    || result.RescheduledCount > 0
                    || result.FailedCount > 0)
                {
                    _logger.LogInformation(
                        "Notification email batch sent={SentCount} expired={ExpiredCount} "
                        + "rescheduled={RescheduledCount} failed={FailedCount}",
                        result.SentCount,
                        result.ExpiredCount,
                        result.RescheduledCount,
                        result.FailedCount);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    "Notification email batch failed with {FailureType}; "
                    + "the next interval will retry.",
                    exception.GetType().Name);
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }
}
