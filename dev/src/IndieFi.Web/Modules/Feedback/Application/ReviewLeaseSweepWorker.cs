using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IndieFi.Web.Modules.Feedback.Application;

public sealed class ReviewLeaseSweepWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReviewLeaseSweepWorker> _logger;
    private readonly TimeSpan _interval;

    public ReviewLeaseSweepWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<ReviewLeaseSweepWorker> logger,
        TimeSpan interval)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        // Injecting the interval lets tests exercise the real timer without waiting a minute.
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
                var sweep = scope.ServiceProvider
                    .GetRequiredService<ReviewLeaseSweepService>();
                var result = await sweep.SweepAsync();

                if (result.ExpiredCount > 0)
                {
                    _logger.LogInformation(
                        "Review lease sweep expired {ExpiredCount} assignments.",
                        result.ExpiredCount);
                }
            }
            // An unhandled exception here stops the whole host by default, so a database blip
            // would take the site down with it. Scope creation is inside the try because it
            // can fail too.
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Review lease sweep failed; the worker will retry on the next interval.");
            }
        }
    }
}
