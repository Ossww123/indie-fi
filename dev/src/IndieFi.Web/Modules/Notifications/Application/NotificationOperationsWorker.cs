using IndieFi.Web.Modules.Notifications.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IndieFi.Web.Modules.Notifications.Application;

public sealed class NotificationOperationsWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly INotificationTransportHealth _transportHealth;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NotificationOperationsWorker> _logger;
    private readonly bool _enabled;
    private readonly TimeSpan _interval;

    public NotificationOperationsWorker(
        IServiceScopeFactory scopeFactory,
        INotificationTransportHealth transportHealth,
        TimeProvider timeProvider,
        ILogger<NotificationOperationsWorker> logger,
        bool enabled,
        TimeSpan interval)
    {
        _scopeFactory = scopeFactory;
        _transportHealth = transportHealth;
        _timeProvider = timeProvider;
        _logger = logger;
        _enabled = enabled;
        _interval = interval;
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
                var service = scope.ServiceProvider
                    .GetRequiredService<NotificationOperationalSnapshotService>();
                var rabbitMqReady = _transportHealth.IsReady();
                var snapshot = await service.GetAsync(
                    _timeProvider.GetUtcNow(),
                    rabbitMqReady,
                    stoppingToken);
                NotificationTelemetry.UpdateOperationalSnapshot(snapshot);
                LogSnapshot(snapshot);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                NotificationTelemetry.RecordOperationsObservationFailure();
                _logger.LogError(
                    "Notification operations observation failed with {FailureType}; "
                    + "the next interval will retry.",
                    exception.GetType().Name);
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    private void LogSnapshot(NotificationOperationalSnapshot snapshot)
    {
        if (!snapshot.RabbitMqReady
            || snapshot.OutboxBacklogCount > 0
            || snapshot.EmailDueCount > 0
            || snapshot.EmailFailedCount > 0)
        {
            _logger.LogWarning(
                "Notification operations rabbitmq_ready={RabbitMqReady} "
                + "outbox_backlog={OutboxBacklogCount} "
                + "oldest_outbox_age_seconds={OldestOutboxAgeSeconds} "
                + "email_pending={EmailPendingCount} email_due={EmailDueCount} "
                + "email_failed={EmailFailedCount} "
                + "oldest_email_due_age_seconds={OldestEmailDueAgeSeconds}",
                snapshot.RabbitMqReady,
                snapshot.OutboxBacklogCount,
                snapshot.OldestOutboxAgeSeconds,
                snapshot.EmailPendingCount,
                snapshot.EmailDueCount,
                snapshot.EmailFailedCount,
                snapshot.OldestEmailDueAgeSeconds);
        }
        else
        {
            _logger.LogDebug(
                "Notification operations healthy email_pending={EmailPendingCount}",
                snapshot.EmailPendingCount);
        }
    }
}
