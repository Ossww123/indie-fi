using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Contracts;
using MassTransit;

namespace IndieFi.Web.Modules.Notifications.Infrastructure;

public sealed class FollowedUserGamePublishedFanoutConsumer :
    IConsumer<FollowedUserGamePublishedFanoutV1>
{
    private readonly FollowedUserGamePublishedFanoutService _fanout;

    public FollowedUserGamePublishedFanoutConsumer(
        FollowedUserGamePublishedFanoutService fanout)
    {
        _fanout = fanout;
    }

    public async Task Consume(
        ConsumeContext<FollowedUserGamePublishedFanoutV1> context)
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
