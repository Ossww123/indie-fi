using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IndieFi.Web.Modules.Accounts.Application;

public sealed class AuthenticationEmailOutboxWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AuthenticationEmailOutboxWorker> _logger;
    private readonly bool _enabled;
    private readonly TimeSpan _interval;
    private readonly int _batchSize;

    public AuthenticationEmailOutboxWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<AuthenticationEmailOutboxWorker> logger,
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
                    .GetRequiredService<AuthenticationEmailOutboxProcessor>();
                var result = await processor.ProcessBatchAsync(_batchSize, stoppingToken);

                if (result.SentCount > 0 || result.ExpiredCount > 0)
                {
                    _logger.LogInformation(
                        "Authentication email Outbox processed {SentCount} sent and " +
                        "{ExpiredCount} expired messages.",
                        result.SentCount,
                        result.ExpiredCount);
                }

                if (result.FailedCount > 0)
                {
                    // Recipient addresses and tokens must stay out of logs, so only the
                    // count is recorded. Each failed message is retried after its delay.
                    _logger.LogWarning(
                        "Authentication email delivery failed for {FailedCount} messages; " +
                        "they are rescheduled.",
                        result.FailedCount);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // SMTP exception messages can contain the recipient address. Record only
                // the exception type so email addresses, tokens, and payloads stay out of
                // logs. The failed row remains in the Outbox for the next interval.
                _logger.LogError(
                    "Authentication email delivery failed with {FailureType}; " +
                    "the worker will retry.",
                    exception.GetType().Name);
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }
}
