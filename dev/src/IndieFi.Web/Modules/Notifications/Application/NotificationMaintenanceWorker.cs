using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IndieFi.Web.Modules.Notifications.Application;

public sealed class NotificationMaintenanceWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NotificationMaintenanceWorker> _logger;
    private readonly bool _enabled;
    private readonly TimeSpan _interval;
    private readonly int _batchSize;

    public NotificationMaintenanceWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<NotificationMaintenanceWorker> logger,
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
                var maintenance = scope.ServiceProvider
                    .GetRequiredService<NotificationMaintenanceService>();
                var result = await maintenance.RunBatchAsync(
                    _batchSize,
                    stoppingToken);
                if (result.TotalCount > 0)
                {
                    _logger.LogInformation(
                        "Notification maintenance removed_content={ContentRemovedCount} "
                        + "deleted_deliveries={DeliveryDeletedCount} "
                        + "deleted_outbox_states={OutboxStateDeletedCount} "
                        + "deleted_inbox_states={InboxStateDeletedCount}",
                        result.ContentRemovedCount,
                        result.DeliveryDeletedCount,
                        result.OutboxStateDeletedCount,
                        result.InboxStateDeletedCount);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    "Notification maintenance failed with {FailureType}; "
                    + "the next interval will retry.",
                    exception.GetType().Name);
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }
}
