using System.Diagnostics;
using MassTransit;

namespace IndieFi.Web.Modules.Notifications.Infrastructure;

public sealed class NotificationConsumeObserver : IConsumeObserver
{
    public Task PreConsume<T>(ConsumeContext<T> context)
        where T : class
    {
        context.GetOrAddPayload(() => new ConsumeTiming(Stopwatch.GetTimestamp()));
        return Task.CompletedTask;
    }

    public Task PostConsume<T>(ConsumeContext<T> context)
        where T : class
    {
        NotificationTelemetry.RecordConsumer<T>(
            "succeeded",
            GetElapsed(context));
        return Task.CompletedTask;
    }

    public Task ConsumeFault<T>(
        ConsumeContext<T> context,
        Exception exception)
        where T : class
    {
        NotificationTelemetry.RecordConsumer<T>(
            "failed",
            GetElapsed(context));
        return Task.CompletedTask;
    }

    private static TimeSpan GetElapsed<T>(ConsumeContext<T> context)
        where T : class =>
        context.TryGetPayload<ConsumeTiming>(out var timing)
            ? Stopwatch.GetElapsedTime(timing.StartTimestamp)
            : TimeSpan.Zero;

    private sealed record ConsumeTiming(long StartTimestamp);
}
