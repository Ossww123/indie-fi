using MassTransit;

namespace IndieFi.Web.Modules.Notifications.Infrastructure;

public sealed class NotificationRetryObserver : IRetryObserver
{
    public Task PostCreate<T>(RetryPolicyContext<T> context)
        where T : class, PipeContext => Task.CompletedTask;

    public Task PostFault<T>(RetryContext<T> context)
        where T : class, PipeContext => Task.CompletedTask;

    public Task PreRetry<T>(RetryContext<T> context)
        where T : class, PipeContext
    {
        NotificationTelemetry.RecordConsumerRetry();
        return Task.CompletedTask;
    }

    public Task RetryFault<T>(RetryContext<T> context)
        where T : class, PipeContext => Task.CompletedTask;

    public Task RetryComplete<T>(RetryContext<T> context)
        where T : class, PipeContext => Task.CompletedTask;
}
