using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IndieFi.Web.Modules.Notifications.Application;

public sealed class KeyDropOpenNotificationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<KeyDropOpenNotificationWorker> _logger;
    private readonly TimeSpan _interval;

    public KeyDropOpenNotificationWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<KeyDropOpenNotificationWorker> logger,
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
                    .GetRequiredService<KeyDropOpenNotificationService>();
                var result = await service.StageDueAsync(
                    cancellationToken: stoppingToken);
                if (result.StagedCount > 0)
                {
                    _logger.LogInformation(
                        "Key drop open notification fan-outs staged={StagedCount}",
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
                    "Key drop open notification staging failed; the next interval will retry.");
            }
        }
    }
}
