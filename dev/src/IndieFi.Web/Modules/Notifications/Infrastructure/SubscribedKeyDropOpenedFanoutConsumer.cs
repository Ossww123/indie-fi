using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Contracts;
using MassTransit;

namespace IndieFi.Web.Modules.Notifications.Infrastructure;

public sealed class SubscribedKeyDropOpenedFanoutConsumer :
    IConsumer<SubscribedKeyDropOpenedFanoutV1>
{
    private readonly SubscribedKeyDropOpenedFanoutService _fanout;

    public SubscribedKeyDropOpenedFanoutConsumer(
        SubscribedKeyDropOpenedFanoutService fanout)
    {
        _fanout = fanout;
    }

    public async Task Consume(ConsumeContext<SubscribedKeyDropOpenedFanoutV1> context)
    {
        var result = await _fanout.StageBatchAsync(
            context.Message,
            context.CancellationToken);
        if (result.NextAfterUserId is not long nextAfterUserId)
        {
            return;
        }

        await context.Publish(
            context.Message with { AfterUserId = nextAfterUserId },
            publish => publish.MessageId = NotificationFanoutMessageId.CreateContinuation(
                context.Message.FanoutId,
                nextAfterUserId),
            context.CancellationToken);
    }
}
