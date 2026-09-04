using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IndieFi.Web.Modules.Feedback.Application;

public sealed class ReviewDeadlineNotificationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReviewDeadlineNotificationWorker> _logger;
    private readonly TimeSpan _interval;

    public ReviewDeadlineNotificationWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<ReviewDeadlineNotificationWorker> logger,
        TimeSpan interval)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _interval = interval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider
                    .GetRequiredService<ReviewDeadlineNotificationService>();
                var result = await service.StageDueAsync(
                    cancellationToken: stoppingToken);
                if (result.StagedCount > 0)
                {
                    _logger.LogInformation(
                        "Review deadline notifications staged={StagedCount}",
                        result.StagedCount);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Review deadline notification staging failed; the next interval will retry.");
            }
        }
    }
}
